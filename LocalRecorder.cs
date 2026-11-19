using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    public class LocalRecorder
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
        
        public bool IsRecording { get; private set; }
        public string ConnectionState { get; private set; } = "OFFLINE";
        public double CurrentFps { get; private set; }
        public double CurrentBitrateKbps { get; private set; }
        public double CurrentSpeed { get; private set; }
        public string CurrentSizeText { get; private set; } = "";
        public string LastError { get; private set; } = "";

        private volatile bool _manualStop = false;
        private static readonly Regex FpsRegex = new(@"fps=\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);
        private static readonly Regex BitrateRegex = new(@"bitrate=\s*([0-9]+(?:\.[0-9]+)?)kbits/s", RegexOptions.Compiled);
        private static readonly Regex SpeedRegex = new(@"speed=\s*([0-9]+(?:\.[0-9]+)?)x", RegexOptions.Compiled);
        private static readonly Regex SizeRegex = new(@"(?:L?size)=\s*([0-9]+(?:\.[0-9]+)?\s*[KMG]i?B)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        public Action<string>? OnLog;
        
        public int Width = 1920;
        public int Height = 1080;
        public int Framerate = 30; 
        
        // Latest valid video frame (atomically swapped on PushFrame) for steady CFR pacing
        private volatile byte[]? _latestVideoFrame;
        
        private static string GetSegmentFormat(string containerExtension)
        {
            string ext = (containerExtension ?? ".mp4").Trim().TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "ts" => "mpegts",
                "mkv" => "matroska",
                "" => "mp4",
                _ => ext
            };
        }

        public void Start(RecordingProfileDefinition profile, string outputDirectory, int width, int height, double fps, bool intervalMode = false, int intervalSeconds = 60)
        {
            if (IsRecording) return;
            
            Width = width;
            Height = height;
            Framerate = (int)Math.Round(fps);
            if (Framerate <= 0) Framerate = 30;
            _latestVideoFrame = null;
            _manualStop = false;
            ConnectionState = "STARTING";
            CurrentFps = 0;
            CurrentBitrateKbps = 0;
            CurrentSpeed = 0;
            CurrentSizeText = "";
            LastError = "";
            
            _cts = new CancellationTokenSource();
            
            _videoQueue = new BlockingCollection<byte[]>(75);
            _audioQueue = new BlockingCollection<byte[]>(300);

            // Create named pipes BEFORE launching FFmpeg so it can connect
            string uniqueId = Guid.NewGuid().ToString("N");
            string videoPipeName = "decklink_video_" + uniqueId;
            string audioPipeName = "decklink_audio_" + uniqueId;

            int pipeBuf = 4 * 1024 * 1024; // 4MB buffer
            _videoPipe = new NamedPipeServerStream(videoPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, pipeBuf, pipeBuf);
            _audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 192000, 192000);
            
            string ffmpegPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");

            string outputOptions = profile.OutputOptions;
            string outputTargetArgs;
            string logTarget;
            if (intervalMode)
            {
                int safeInterval = Math.Max(1, intervalSeconds);
                string pattern = Path.Combine(outputDirectory, $"{profile.FileNameSuffix}_%d%m%Y_%H%M%S{profile.ContainerExtension}");
                logTarget = pattern;
                string keyframeArg = profile.UseDualMonoAudio ? "" : $"-force_key_frames \"expr:gte(t,n_forced*{safeInterval})\" ";
                string segOptsArg = "";
                if (profile.UseDualMonoAudio && outputOptions.Contains("-signal_standard smpte274m", StringComparison.Ordinal))
                {
                    outputOptions = outputOptions.Replace("-signal_standard smpte274m", "").Trim();
                    segOptsArg = "-segment_format_options \"signal_standard=smpte274m\" ";
                }
                outputTargetArgs = $"{keyframeArg}-f segment -segment_time {safeInterval} -reset_timestamps 1 -segment_start_number 0 -segment_format \"{GetSegmentFormat(profile.ContainerExtension)}\" {segOptsArg}-strftime 1 \"{pattern}\"";
            }
            else
            {
                string timestamp = DateTime.Now.ToString("ddMMyyyy_HHmmss");
                string fileName = $"{profile.FileNameSuffix}_{timestamp}{profile.ContainerExtension}";
                string outputPath = Path.Combine(outputDirectory, fileName);
                logTarget = outputPath;
                outputTargetArgs = $"\"{outputPath}\"";
            }
            
            string videoFilterArg = string.IsNullOrWhiteSpace(profile.VideoFilter) ? "" : $"-vf \"{profile.VideoFilter}\" ";
            
            string videoPipePath = $"\\\\.\\pipe\\{videoPipeName}";
            string audioPipePath = $"\\\\.\\pipe\\{audioPipeName}";
            
            string audioMappingArgs = profile.UseDualMonoAudio
                ? "-filter_complex \"[1:a]asplit=2[rec_a1_src][rec_a2_src];[rec_a1_src]pan=mono|c0=c0[rec_a1];[rec_a2_src]pan=mono|c0=c1[rec_a2]\" -map 0:v -map \"[rec_a1]\" -map \"[rec_a2]\" "
                : "";

            // Input is native 1920x1080 UYVY
            string args = $"-y " +
                          $"-f rawvideo -vcodec rawvideo -pix_fmt uyvy422 -s {Width}x{Height} -r {Framerate} -thread_queue_size 1024 -probesize 32 -analyzeduration 0 -i \"{videoPipePath}\" " +
                          $"-f s16le -ac 2 -ar 48000 -thread_queue_size 1024 -probesize 32 -analyzeduration 0 -i \"{audioPipePath}\" " +
                          $"{audioMappingArgs}{videoFilterArg}{outputOptions} {outputTargetArgs}";

            OnLog?.Invoke($"FFmpeg args: {args}");

            _ffmpegProcess = new Process();
            var currentProcess = _ffmpegProcess;
            currentProcess.StartInfo.FileName = ffmpegPath;
            currentProcess.StartInfo.Arguments = args;
            currentProcess.StartInfo.UseShellExecute = false;
            currentProcess.StartInfo.CreateNoWindow = true;
            currentProcess.StartInfo.RedirectStandardError = true;
            currentProcess.StartInfo.RedirectStandardInput = true;
            
            currentProcess.ErrorDataReceived += (s, e) => {
                if (e.Data == null) return;
                ParseFfmpegTelemetry(e.Data);
                OnLog?.Invoke("FFmpeg: " + e.Data);
            };
            
            currentProcess.EnableRaisingEvents = true;
            currentProcess.Exited += (s, e) => {
                int code = -1;
                try { code = currentProcess.ExitCode; } catch { }
                OnLog?.Invoke($"FFmpeg process exited with code {code}");
                if (_ffmpegProcess == currentProcess) {
                    IsRecording = false;
                    ConnectionState = (_manualStop || code == 0) ? "OFFLINE" : "ERROR";
                    if (ConnectionState == "ERROR" && string.IsNullOrEmpty(LastError))
                        LastError = $"Exited (code {code})";
                    CurrentFps = 0;
                    CurrentBitrateKbps = 0;
                    CurrentSpeed = 0;
                    CurrentSizeText = "";
                }
            };

            try { currentProcess.Start(); currentProcess.BeginErrorReadLine(); } catch (Exception ex) { OnLog?.Invoke("Failed to start FFmpeg: " + ex.Message); IsRecording = false; ConnectionState = "ERROR"; LastError = ex.Message; return; }
            
            IsRecording = true;
            
            _videoTask = Task.Run(() => VideoPipeWorker(_cts.Token)); _audioTask = Task.Run(() => AudioPipeWorker(_cts.Token));
            // Exact CFR frame pacer - guarantees steady video/audio delivery to FFmpeg
            _feedTask = Task.Run(() => PacerWorker(_cts.Token));
            
            OnLog?.Invoke($"Recording started ({(intervalMode ? $"Interval {Math.Max(1, intervalSeconds)}s" : "Infinite")}). Resolution: {Width}x{Height} @ {Framerate}fps -> {logTarget}");
        }
        
        public void Stop()
        {
            if (!IsRecording) return;
            _manualStop = true;
            IsRecording = false;
            ConnectionState = "OFFLINE";
            CurrentFps = 0;
            CurrentBitrateKbps = 0;
            CurrentSpeed = 0;
            CurrentSizeText = "";
            
            _cts?.Cancel();
            
            try 
            { 
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    try 
                    { 
                        _ffmpegProcess.StandardInput.WriteLine("q"); 
                        _ffmpegProcess.StandardInput.Flush();
                    } 
                    catch { }
                }
            } 
            catch { }

            // Immediately close and dispose the named pipes!
            // This unblocks FFmpeg's ReadFile calls with EOF (ERROR_BROKEN_PIPE),
            // allowing FFmpeg to finalize the MP4 moov atom and exit cleanly without hanging.
            try { _videoPipe?.Dispose(); } catch { }
            try { _audioPipe?.Dispose(); } catch { }
            _videoPipe = null;
            _audioPipe = null;

            try 
            { 
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    bool exited = _ffmpegProcess.WaitForExit(5000);
                    if (!exited && !_ffmpegProcess.HasExited)
                    {
                        try { _ffmpegProcess.Kill(); } catch { }
                    }
                }
            } 
            catch { }
            
            OnLog?.Invoke("Recording stopped.");
        }

        private void ParseFfmpegTelemetry(string line)
        {
            if (line.Contains("frame=") && line.Contains("fps="))
            {
                var fpsMatch = FpsRegex.Match(line);
                if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedFps))
                    CurrentFps = parsedFps;

                var brMatch = BitrateRegex.Match(line);
                if (brMatch.Success && double.TryParse(brMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedBr))
                    CurrentBitrateKbps = parsedBr;

                var spdMatch = SpeedRegex.Match(line);
                if (spdMatch.Success && double.TryParse(spdMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedSpd))
                    CurrentSpeed = parsedSpd;

                var szMatch = SizeRegex.Match(line);
                if (szMatch.Success)
                    CurrentSizeText = szMatch.Groups[1].Value.Replace(" ", "");

                if (IsRecording && !_manualStop)
                    ConnectionState = "RECORDING";
                return;
            }

            if (line.Contains("Error opening output", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("No space left", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Could not write header", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionState = "ERROR";
                LastError = line.Trim();
            }
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

        public void PushFrame(byte[] audio, byte[] video)
        {
            if (!IsRecording) return;

            if (video != null)
            {
                _latestVideoFrame = video;
            }

            if (audio != null && _audioQueue.Count < _audioQueue.BoundedCapacity - 4)
            {
                try { _audioQueue.TryAdd(audio, 0); } catch { }
            }
        }

        // Generates steady CFR video (repeating the latest valid frame or black) and fills
        // audio gaps with silence so FFmpeg always receives exact real-time framerate.
        private void PacerWorker(CancellationToken token)
        {
            int audioSamplesPerFrame = 48000 / Math.Max(1, Framerate);
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
            
            OnLog?.Invoke($"Recording CFR pacer started ({Framerate} fps).");

            double ticksPerFrame = (double)Stopwatch.Frequency / Math.Max(1, Framerate);
            double nextTick = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested && IsRecording)
            {
                long now = Stopwatch.GetTimestamp();
                if (now - nextTick > Stopwatch.Frequency * 0.5)
                {
                    nextTick = now;
                }

                byte[] vFrame = _latestVideoFrame ?? blackFrame;
                if (_videoQueue.Count < 15)
                {
                    try { _videoQueue.TryAdd(vFrame, 0); } catch { }
                }

                if (_audioQueue.Count == 0)
                {
                    try { _audioQueue.TryAdd(silentAudio, 0); } catch { }
                }

                nextTick += ticksPerFrame;
                while (!token.IsCancellationRequested && IsRecording)
                {
                    long remaining = (long)nextTick - Stopwatch.GetTimestamp();
                    if (remaining <= 0) break;
                    double remainingMs = remaining * 1000.0 / Stopwatch.Frequency;
                    if (remainingMs > 16.5)
                        Thread.Sleep(1);
                    else if (remainingMs > 0.5)
                        Thread.Yield();
                    else
                        Thread.SpinWait(50);
                }
            }
        }

        private async Task VideoPipeWorker(CancellationToken token) { try { OnLog?.Invoke("Video pipe: waiting for FFmpeg..."); await _videoPipe!.WaitForConnectionAsync(token); OnLog?.Invoke("Video pipe: connected."); while (_videoQueue.TryTake(out _)) { } while (!token.IsCancellationRequested) { byte[] frame = _videoQueue.Take(token); await _videoPipe.WriteAsync(frame, 0, frame.Length, token); } } catch { } } private async Task AudioPipeWorker(CancellationToken token) { try { OnLog?.Invoke("Audio pipe: waiting for FFmpeg..."); await _audioPipe!.WaitForConnectionAsync(token); OnLog?.Invoke("Audio pipe: connected."); while (_audioQueue.TryTake(out _)) { } while (!token.IsCancellationRequested) { byte[] frame = _audioQueue.Take(token); await _audioPipe.WriteAsync(frame, 0, frame.Length, token); } } catch { } }
    }
}







