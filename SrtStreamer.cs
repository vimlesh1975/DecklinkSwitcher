using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    public class SrtStreamer
    {
        private Process? _ffmpegProcess;
        private NamedPipeServerStream? _videoPipe;
        private NamedPipeServerStream? _audioPipe;
        
        private BlockingCollection<byte[]> _videoQueue = new BlockingCollection<byte[]>(30);
        private BlockingCollection<byte[]> _audioQueue = new BlockingCollection<byte[]>(200);
        
        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private Task? _audioTask;
        private Task? _feedTask;
        
        public bool IsStreaming { get; private set; }
        
        public Action<string>? OnLog;
        public Action<bool>? OnStateChanged;
        
        public int Width = 1920;
        public int Height = 1080;
        public int Framerate = 25; 
        
        // Set to true once real frames are being pushed
        private volatile bool _hasRealFrames = false;
        private long _lastVideoPushTick = 0;
        
        private static string ResolveFfmpegPath()
        {
            string baseDirFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(baseDirFfmpeg)) return baseDirFfmpeg;
            
            string projectBinFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "win-x64", "ffmpeg.exe");
            if (File.Exists(projectBinFfmpeg)) return projectBinFfmpeg;

            return "ffmpeg.exe";
        }

        public void Start(string srtUrl, int width, int height, double fps)
        {
            if (IsStreaming) return;
            
            Width = width;
            Height = height;
            Framerate = (int)Math.Round(fps);
            if (Framerate <= 0) Framerate = 25; 
            _hasRealFrames = false;
            _lastVideoPushTick = 0;
            
            _cts = new CancellationTokenSource();
            
            _videoQueue = new BlockingCollection<byte[]>(30);
            _audioQueue = new BlockingCollection<byte[]>(200);

            // Create named pipes BEFORE launching FFmpeg so it can connect
            string uniqueId = Guid.NewGuid().ToString("N");
            string videoPipeName = "decklink_srt_video_" + uniqueId;
            string audioPipeName = "decklink_srt_audio_" + uniqueId;

            int pipeBuf = 4 * 1024 * 1024; // 4MB buffer
            _videoPipe = new NamedPipeServerStream(videoPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, pipeBuf, pipeBuf);
            _audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 192000, 192000);
            
            string ffmpegPath = ResolveFfmpegPath();

            string videoPipePath = $@"\\.\pipe\{videoPipeName}";
            string audioPipePath = $@"\\.\pipe\{audioPipeName}";
            
            // Ultra-low latency H.264 (CPU libx264, ultrafast, zerolatency, 6 Mbps) over MPEG-TS/SRT
            string args = $"-y " +
                          $"-f rawvideo -vcodec rawvideo -pix_fmt uyvy422 -s {Width}x{Height} -r {Framerate} -thread_queue_size 1024 -probesize 32 -analyzeduration 0 -i \"{videoPipePath}\" " +
                          $"-f s16le -ac 2 -ar 48000 -thread_queue_size 1024 -probesize 32 -analyzeduration 0 -i \"{audioPipePath}\" " +
                          $"-c:v libx264 -preset ultrafast -tune zerolatency -b:v 6000k -maxrate 6000k -bufsize 12000k -pix_fmt yuv420p -g {Framerate * 2} " +
                          $"-c:a aac -b:a 192k -ar 48000 -ac 2 " +
                          $"-f mpegts -flush_packets 1 -fflags +nobuffer+flush_packets \"{srtUrl}\"";

            OnLog?.Invoke($"FFmpeg SRT args: {args}");

            _ffmpegProcess = new Process();
            var currentProcess = _ffmpegProcess;
            currentProcess.StartInfo.FileName = ffmpegPath;
            currentProcess.StartInfo.Arguments = args;
            currentProcess.StartInfo.UseShellExecute = false;
            currentProcess.StartInfo.CreateNoWindow = true;
            currentProcess.StartInfo.RedirectStandardError = true;
            currentProcess.StartInfo.RedirectStandardInput = true;
            
            currentProcess.ErrorDataReceived += (s, e) => {
                if (e.Data != null) OnLog?.Invoke("FFmpeg SRT: " + e.Data);
            };
            
            currentProcess.EnableRaisingEvents = true;
            currentProcess.Exited += (s, e) => {
                int code = -1;
                try { code = currentProcess.ExitCode; } catch { }
                OnLog?.Invoke($"FFmpeg SRT process exited with code {code}");
                if (_ffmpegProcess == currentProcess) {
                    IsStreaming = false;
                    OnStateChanged?.Invoke(false);
                }
            };

            try 
            { 
                currentProcess.Start(); 
                currentProcess.BeginErrorReadLine(); 
            } 
            catch (Exception ex) 
            { 
                OnLog?.Invoke("Failed to start FFmpeg for SRT: " + ex.Message); 
                IsStreaming = false; 
                OnStateChanged?.Invoke(false);
                return; 
            }
            
            IsStreaming = true;
            OnStateChanged?.Invoke(true);
            
            _videoTask = Task.Run(() => VideoPipeWorker(_cts.Token));
            _audioTask = Task.Run(() => AudioPipeWorker(_cts.Token));
            // Fallback frame feeder - keeps FFmpeg alive even when no DeckLink frames arrive
            _feedTask = Task.Run(() => FallbackFeeder(_cts.Token));
            
            OnLog?.Invoke($"SRT streaming started. Resolution: {Width}x{Height} @ {Framerate}fps -> {srtUrl}");
        }
        
        public void Stop()
        {
            if (!IsStreaming) return;
            
            try 
            { 
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    try { _ffmpegProcess.StandardInput.WriteLine("q"); } catch { }
                    _ffmpegProcess.WaitForExit(3000);
                    if (!_ffmpegProcess.HasExited) _ffmpegProcess.Kill();
                }
            } 
            catch { }

            IsStreaming = false;
            _cts?.Cancel();
            
            try { _videoPipe?.Dispose(); } catch { }
            try { _audioPipe?.Dispose(); } catch { }
            _videoPipe = null;
            _audioPipe = null;
            
            OnStateChanged?.Invoke(false);
            OnLog?.Invoke("SRT streaming stopped.");
        }

        public void UpdateDimensions(int width, int height)
        {
            if (Width != width || Height != height)
            {
                OnLog?.Invoke($"SRT frame size changed: {Width}x{Height} -> {width}x{height}. Restart stream to apply new resolution.");
                Width = width;
                Height = height;
            }
        }

        public void PushFrame(byte[]? audio, byte[]? video)
        {
            if (!IsStreaming) return;
            
            if (video != null)
            {
                _hasRealFrames = true;
                Interlocked.Exchange(ref _lastVideoPushTick, Environment.TickCount64);
            }
            
            // Drop BOTH if EITHER queue is getting full, to preserve exact A/V sync!
            if ((video != null && _videoQueue.Count >= _videoQueue.BoundedCapacity - 2) || 
                (audio != null && _audioQueue.Count >= _audioQueue.BoundedCapacity - 2))
            {
                // Congested: drop chunk
                return; 
            }

            if (video != null)
            {
                try { _videoQueue.TryAdd(video, 0); } catch { }
            }
            if (audio != null)
            {
                try { _audioQueue.TryAdd(audio, 0); } catch { }
            }
        }

        // Generates UYVY black frames (1920x1080) when no real frames arrive,
        // so FFmpeg stays alive and keeps the SRT connection open.
        private void FallbackFeeder(CancellationToken token)
        {
            int frameMs = 1000 / Framerate;
            if (frameMs <= 0) frameMs = 40;
            int audioSamplesPerFrame = 48000 / Framerate;
            int frameSize = Width * Height * 2; // UYVY is 2 bytes per pixel
            
            // Black UYVY frame: U=128, Y=16, V=128, Y=16
            byte[] blackFrame = new byte[frameSize];
            for (int i = 0; i < frameSize; i += 4)
            {
                blackFrame[i + 0] = 128; // U
                blackFrame[i + 1] = 16;  // Y1
                blackFrame[i + 2] = 128; // V
                blackFrame[i + 3] = 16;  // Y2
            }
            
            // Silent audio
            byte[] silentAudio = new byte[audioSamplesPerFrame * 4]; // 2ch, 16-bit
            
            OnLog?.Invoke("SRT fallback feeder started. Will send black frames if no DeckLink signal.");

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
                OnLog?.Invoke("SRT Video pipe: waiting for FFmpeg...");
                await _videoPipe!.WaitForConnectionAsync(token);
                OnLog?.Invoke("SRT Video pipe: connected.");
                while (_videoQueue.TryTake(out _)) { }
                while (!token.IsCancellationRequested)
                {
                    byte[] frame = _videoQueue.Take(token);
                    await _videoPipe.WriteAsync(frame, 0, frame.Length, token);
                }
            }
            catch { }
        }

        private async Task AudioPipeWorker(CancellationToken token)
        {
            try
            {
                OnLog?.Invoke("SRT Audio pipe: waiting for FFmpeg...");
                await _audioPipe!.WaitForConnectionAsync(token);
                OnLog?.Invoke("SRT Audio pipe: connected.");
                while (_audioQueue.TryTake(out _)) { }
                while (!token.IsCancellationRequested)
                {
                    byte[] frame = _audioQueue.Take(token);
                    await _audioPipe.WriteAsync(frame, 0, frame.Length, token);
                }
            }
            catch { }
        }
    }
}
