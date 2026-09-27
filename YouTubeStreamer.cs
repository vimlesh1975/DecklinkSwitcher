using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    public class YouTubeStreamer
    {
        private Process? _ffmpegProcess;
        private NamedPipeServerStream? _videoPipe;
        private NamedPipeServerStream? _audioPipe;
        
        private BlockingCollection<byte[]> _videoQueue = new BlockingCollection<byte[]>(30);
        private BlockingCollection<byte[]> _audioQueue = new BlockingCollection<byte[]>(100);
        
        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private Task? _audioTask;
        private Task? _feedTask;
        
        public bool IsStreaming { get; private set; }
        
        public Action<string>? OnLog;
        
        public int Width = 1920;
        public int Height = 1080;
        public int Framerate = 30; 
        
        // Set to true once real frames are being pushed
        private volatile bool _hasRealFrames = false;
        private long _lastVideoPushTick = 0;
        
        public void Start(string streamKey, int width, int height, double fps)
        {
            if (IsStreaming) return;
            
            Width = width;
            Height = height;
            Framerate = (int)Math.Round(fps);
            if (Framerate <= 0) Framerate = 30; 
            _hasRealFrames = false;
            _lastVideoPushTick = 0;
            
            _cts = new CancellationTokenSource();
            
            _videoQueue = new BlockingCollection<byte[]>(30);
            _audioQueue = new BlockingCollection<byte[]>(100);

            // Create named pipes BEFORE launching FFmpeg so it can connect
            string videoPipeName = "decklink_video_" + Process.GetCurrentProcess().Id;
            string audioPipeName = "decklink_audio_" + Process.GetCurrentProcess().Id;

            int pipeBuf = 4 * 1024 * 1024; // 4MB buffer
            _videoPipe = new NamedPipeServerStream(videoPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, pipeBuf, pipeBuf);
            _audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 192000, 192000);
            
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath)) 
            {
                OnLog?.Invoke("ffmpeg.exe not found in output directory: " + AppDomain.CurrentDomain.BaseDirectory);
                return;
            }

            string rtmpUrl = "rtmp://a.rtmp.youtube.com/live2/" + streamKey;
            string videoPipePath = $"\\\\.\\pipe\\{videoPipeName}";
            string audioPipePath = $"\\\\.\\pipe\\{audioPipeName}";
            
            // bgra = 4 bytes per pixel, input at 480x270, FFmpeg scales to 1080p
            string args = $"-y " +
                          $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s 480x270 -r {Framerate} -i \"{videoPipePath}\" " +
                          $"-f s16le -ac 2 -ar 48000 -i \"{audioPipePath}\" " +
                          $"-vf scale=1920:1080:flags=bilinear " +
                          $"-c:v libx264 -preset ultrafast -b:v 6800k -maxrate 6800k -bufsize 13600k -pix_fmt yuv420p -g {Framerate * 2} " +
                          $"-c:a aac -b:a 128k -f flv \"{rtmpUrl}\"";

            OnLog?.Invoke($"FFmpeg args: {args}");

            _ffmpegProcess = new Process();
            _ffmpegProcess.StartInfo.FileName = ffmpegPath;
            _ffmpegProcess.StartInfo.Arguments = args;
            _ffmpegProcess.StartInfo.UseShellExecute = false;
            _ffmpegProcess.StartInfo.CreateNoWindow = true;
            _ffmpegProcess.StartInfo.RedirectStandardError = true;
            
            _ffmpegProcess.ErrorDataReceived += (s, e) => {
                if (e.Data != null) OnLog?.Invoke("FFmpeg: " + e.Data);
            };
            
            _ffmpegProcess.EnableRaisingEvents = true;
            _ffmpegProcess.Exited += (s, e) => {
                int code = -1;
                try { code = _ffmpegProcess?.ExitCode ?? -1; } catch { }
                OnLog?.Invoke($"FFmpeg process exited with code {code}");
                IsStreaming = false;
            };

            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();
            
            IsStreaming = true;
            
            _videoTask = Task.Run(() => VideoPipeWorker(_cts.Token));
            _audioTask = Task.Run(() => AudioPipeWorker(_cts.Token));
            // Fallback frame feeder - keeps FFmpeg alive even when no DeckLink frames arrive
            _feedTask = Task.Run(() => FallbackFeeder(_cts.Token));
            
            OnLog?.Invoke($"Streaming started. Resolution: {Width}x{Height} @ {Framerate}fps -> {rtmpUrl}");
        }
        
        public void Stop()
        {
            if (!IsStreaming) return;
            IsStreaming = false;
            _cts?.Cancel();
            
            try { _ffmpegProcess?.Kill(); } catch { }
            try { _videoPipe?.Dispose(); } catch { }
            try { _audioPipe?.Dispose(); } catch { }
            _videoPipe = null;
            _audioPipe = null;
            
            OnLog?.Invoke("Streaming stopped.");
        }

        public void UpdateDimensions(int width, int height)
        {
            if (Width != width || Height != height)
            {
                OnLog?.Invoke($"Stream frame size changed: {Width}x{Height} -> {width}x{height}. Restart stream to apply new resolution.");
                Width = width;
                Height = height;
            }
        }

        public void PushVideo(byte[] frame)
        {
            if (!IsStreaming) return;
            _hasRealFrames = true;
            Interlocked.Exchange(ref _lastVideoPushTick, Environment.TickCount64);
            // Drop if full to avoid blocking the DeckLink callback thread
            if (_videoQueue.Count < _videoQueue.BoundedCapacity - 2)
            {
                try { _videoQueue.TryAdd(frame, 0); } catch { }
            }
        }

        public void PushAudio(byte[] pcm)
        {
            if (!IsStreaming) return;
            if (_audioQueue.Count < _audioQueue.BoundedCapacity - 2)
            {
                try { _audioQueue.TryAdd(pcm, 0); } catch { }
            }
        }

        // Generates BGRA black frames (480x270) when no real frames arrive,
        // so FFmpeg stays alive and keeps the RTMP connection open.
        private void FallbackFeeder(CancellationToken token)
        {
            int frameMs = 1000 / Framerate;
            int audioSamplesPerFrame = 48000 / Framerate;
            int frameSize = 480 * 270 * 4; // BGRA 480x270
            
            // Black BGRA frame: B=0, G=0, R=0, A=255
            byte[] blackFrame = new byte[frameSize];
            for (int i = 0; i < frameSize; i += 4)
            {
                blackFrame[i + 0] = 0;   // B
                blackFrame[i + 1] = 0;   // G
                blackFrame[i + 2] = 0;   // R
                blackFrame[i + 3] = 255; // A
            }
            
            // Silent audio
            byte[] silentAudio = new byte[audioSamplesPerFrame * 4]; // 2ch, 16-bit
            
            OnLog?.Invoke("Fallback feeder started. Will send black frames if no DeckLink signal.");

            while (!token.IsCancellationRequested && IsStreaming)
            {
                long now = Environment.TickCount64;
                bool realFrameRecent = _hasRealFrames && (now - Interlocked.Read(ref _lastVideoPushTick)) < 500;
                
                if (!realFrameRecent)
                {
                    // No real frame in 500ms, push black fallback
                    if (_videoQueue.Count < 5)
                    {
                        try { _videoQueue.TryAdd((byte[])blackFrame.Clone(), 0); } catch { }
                    }
                    if (_audioQueue.Count < 20)
                    {
                        try { _audioQueue.TryAdd((byte[])silentAudio.Clone(), 0); } catch { }
                    }
                }
                
                Thread.Sleep(frameMs);
            }
        }

        private async Task VideoPipeWorker(CancellationToken token)
        {
            try
            {
                OnLog?.Invoke("Video pipe: waiting for FFmpeg to connect...");
                await _videoPipe!.WaitForConnectionAsync(token);
                OnLog?.Invoke("Video pipe: FFmpeg connected. Streaming video...");
                while (!token.IsCancellationRequested)
                {
                    byte[] frame = _videoQueue.Take(token);
                    await _videoPipe.WriteAsync(frame, 0, frame.Length, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnLog?.Invoke("Video pipe error: " + ex.Message);
            }
        }

        private async Task AudioPipeWorker(CancellationToken token)
        {
            try
            {
                OnLog?.Invoke("Audio pipe: waiting for FFmpeg to connect...");
                await _audioPipe!.WaitForConnectionAsync(token);
                OnLog?.Invoke("Audio pipe: FFmpeg connected. Streaming audio...");
                while (!token.IsCancellationRequested)
                {
                    byte[] pcm = _audioQueue.Take(token);
                    await _audioPipe.WriteAsync(pcm, 0, pcm.Length, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnLog?.Invoke("Audio pipe error: " + ex.Message);
            }
        }
    }
}
