using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    /// <summary>
    /// Receives a live SRT stream (caller or listener) via FFmpeg and outputs
    /// 1920x1080 UYVY video frames and 48kHz stereo S16LE audio into the switcher.
    /// </summary>
    public class SrtSource : IDisposable
    {
        private const int Width = 1920;
        private const int Height = 1080;
        private const int PreviewWidth = 480;
        private const int PreviewHeight = 270;
        private const int VideoFrameBytes = Width * Height * 2;
        private const int AudioChunkSamples = 1920; // 40ms @ 48kHz
        private const int AudioChunkBytes = AudioChunkSamples * 4;

        private Process? _ffmpegProcess;
        private NamedPipeServerStream? _videoPipe;
        private NamedPipeServerStream? _audioPipe;
        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private Task? _audioTask;

        private readonly IntPtr _videoBuffer;
        private readonly byte[] _uyvyManaged = new byte[VideoFrameBytes];
        private readonly byte[] _previewBgra = new byte[PreviewWidth * PreviewHeight * 4];
        private readonly byte[] _pgmBgra = new byte[Width * Height * 4];
        private readonly object _frameLock = new();
        private volatile bool _hasFrame = false;
        private volatile bool _manualStop = false;

        public Action<byte[]>? OnPreviewBufferUpdated;
        public Action<IntPtr, int>? OnUyvy1080Frame;
        public Action<double, double>? OnAudioLevelArrived;
        public Action<string, bool, bool>? OnStatusChanged; // (statusText, isLive, isError)

        public string AudioChannelName { get; set; } = "SRT";
        public float AudioLevel { get; set; } = 1.0f;
        public bool IsConnecting { get; private set; }
        public bool IsStreaming { get; private set; }
        public string CurrentUrl { get; private set; } = "";

        public SrtSource()
        {
            _videoBuffer = Marshal.AllocCoTaskMem(VideoFrameBytes);
        }

        public static string BuildSrtUrl(string mode, string host, int port, int latencyMs, string streamId, string passphrase)
        {
            string m = string.Equals(mode, "Listener", StringComparison.OrdinalIgnoreCase) ? "listener" : "caller";
            string h = (host ?? "").Trim();
            if (string.IsNullOrWhiteSpace(h))
                h = m == "listener" ? "0.0.0.0" : "127.0.0.1";
            else if (m == "listener" && (h == "127.0.0.1" || h == "localhost"))
                h = "0.0.0.0";

            int p = port > 0 ? port : 9000;
            long latencyUs = Math.Max(0, latencyMs) * 1000L;

            var queryParams = new System.Collections.Generic.List<string>
            {
                $"mode={m}",
                $"latency={latencyUs}"
            };

            string sid = (streamId ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sid))
                queryParams.Add($"streamid={Uri.EscapeDataString(sid)}");

            if (!string.IsNullOrWhiteSpace(passphrase))
            {
                queryParams.Add($"passphrase={Uri.EscapeDataString(passphrase)}");
                queryParams.Add("pbkeylen=16");
            }

            return $"srt://{h}:{p}?{string.Join("&", queryParams)}";
        }

        private static string ResolveFfmpegPath()
        {
            string baseDirFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(baseDirFfmpeg)) return baseDirFfmpeg;

            string projectBinFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "win-x64", "ffmpeg.exe");
            if (File.Exists(projectBinFfmpeg)) return projectBinFfmpeg;

            return "ffmpeg.exe";
        }

        public Task StartAsync(string srtUrl)
        {
            Stop();

            string trimmed = (srtUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                OnStatusChanged?.Invoke("NO URL", false, true);
                return Task.CompletedTask;
            }

            if (!trimmed.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "srt://" + trimmed;
            }

            CurrentUrl = trimmed;
            _manualStop = false;
            _hasFrame = false;
            IsConnecting = true;
            IsStreaming = false;
            OnStatusChanged?.Invoke("CONNECTING...", false, false);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            string uniqueId = Guid.NewGuid().ToString("N");
            string videoPipeName = "srt_in_video_" + uniqueId;
            string audioPipeName = "srt_in_audio_" + uniqueId;

            _videoPipe = new NamedPipeServerStream(
                videoPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4 * 1024 * 1024,
                4 * 1024 * 1024);

            _audioPipe = new NamedPipeServerStream(
                audioPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                192000,
                192000);

            string videoPipePath = $"\\\\.\\pipe\\{videoPipeName}";
            string audioPipePath = $"\\\\.\\pipe\\{audioPipeName}";
            string ffmpegPath = ResolveFfmpegPath();

            string args =
                $"-y -fflags +nobuffer+discardcorrupt -flags low_delay -analyzeduration 1000000 -probesize 500000 " +
                $"-i \"{trimmed}\" " +
                $"-map 0:v:0 -vf \"scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=25,format=uyvy422\" " +
                $"-f rawvideo -pix_fmt uyvy422 \"{videoPipePath}\" " +
                $"-map 0:a:0? -vn -ar 48000 -ac 2 -f s16le \"{audioPipePath}\"";

            var proc = new Process();
            proc.StartInfo.FileName = ffmpegPath;
            proc.StartInfo.Arguments = args;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.EnableRaisingEvents = true;

            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("Error opening input", StringComparison.OrdinalIgnoreCase) ||
                    e.Data.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_manualStop && !IsStreaming)
                    {
                        IsConnecting = false;
                        OnStatusChanged?.Invoke("ERROR", false, true);
                    }
                }
            };

            proc.Exited += (s, e) =>
            {
                int code = -1;
                try { code = proc.ExitCode; } catch { }
                if (_ffmpegProcess == proc)
                {
                    IsStreaming = false;
                    IsConnecting = false;
                    bool isErr = !_manualStop && code != 0;
                    OnStatusChanged?.Invoke(isErr ? "DISCONNECTED" : "OFFLINE", false, isErr);
                }
            };

            _ffmpegProcess = proc;

            try
            {
                proc.Start();
                proc.BeginErrorReadLine();
            }
            catch
            {
                IsConnecting = false;
                IsStreaming = false;
                OnStatusChanged?.Invoke("FFMPEG ERR", false, true);
                return Task.CompletedTask;
            }

            _videoTask = Task.Run(() => VideoPipeReaderLoop(_videoPipe, token), token);
            _audioTask = Task.Run(() => AudioPipeReaderLoop(_audioPipe, token), token);
            return Task.CompletedTask;
        }

        public void Stop()
        {
            _manualStop = true;
            IsConnecting = false;
            IsStreaming = false;

            try { _cts?.Cancel(); } catch { }

            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill(entireProcessTree: true);
                    _ffmpegProcess.WaitForExit(1500);
                }
            }
            catch { }
            _ffmpegProcess = null;

            try { _videoPipe?.Dispose(); } catch { }
            try { _audioPipe?.Dispose(); } catch { }
            _videoPipe = null;
            _audioPipe = null;

            OnAudioLevelArrived?.Invoke(0, 0);
            OnStatusChanged?.Invoke("OFFLINE", false, false);
        }

        private async Task VideoPipeReaderLoop(NamedPipeServerStream pipe, CancellationToken token)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token);
                byte[] frameBuf = new byte[VideoFrameBytes];

                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    if (!await ReadExactAsync(pipe, frameBuf, VideoFrameBytes, token))
                        break;

                    lock (_frameLock)
                    {
                        Buffer.BlockCopy(frameBuf, 0, _uyvyManaged, 0, VideoFrameBytes);
                        Marshal.Copy(frameBuf, 0, _videoBuffer, VideoFrameBytes);
                        DownscaleUyvyToPreviewBgra(frameBuf, _previewBgra);
                        _hasFrame = true;
                    }

                    if (!IsStreaming && !_manualStop)
                    {
                        IsConnecting = false;
                        IsStreaming = true;
                        OnStatusChanged?.Invoke("LIVE", true, false);
                    }

                    OnUyvy1080Frame?.Invoke(_videoBuffer, VideoFrameBytes);

                    byte[] previewCopy = new byte[_previewBgra.Length];
                    Buffer.BlockCopy(_previewBgra, 0, previewCopy, 0, previewCopy.Length);
                    OnPreviewBufferUpdated?.Invoke(previewCopy);
                }
            }
            catch { }
        }

        private async Task AudioPipeReaderLoop(NamedPipeServerStream pipe, CancellationToken token)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token);
                byte[] audioBuf = new byte[AudioChunkBytes];

                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    if (!await ReadExactAsync(pipe, audioBuf, AudioChunkBytes, token))
                        break;

                    short[] samples = new short[AudioChunkSamples * 2];
                    Buffer.BlockCopy(audioBuf, 0, samples, 0, AudioChunkBytes);
                    MainWindow.PushAudioPacket(AudioChannelName, samples);

                    // Compute stereo peak level (0..100) for meter display
                    int maxL = 0, maxR = 0;
                    for (int i = 0; i < AudioChunkBytes; i += 4)
                    {
                        short sL = (short)(audioBuf[i] | (audioBuf[i + 1] << 8));
                        short sR = (short)(audioBuf[i + 2] | (audioBuf[i + 3] << 8));
                        int aL = sL == short.MinValue ? 32767 : Math.Abs(sL);
                        int aR = sR == short.MinValue ? 32767 : Math.Abs(sR);
                        if (aL > maxL) maxL = aL;
                        if (aR > maxR) maxR = aR;
                    }
                    OnAudioLevelArrived?.Invoke((maxL / 32767.0) * 100.0, (maxR / 32767.0) * 100.0);
                }
            }
            catch { }
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, token);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        private static void DownscaleUyvyToPreviewBgra(byte[] uyvy, byte[] bgra480)
        {
            // Sample every 4th row and every 4th pixel (every 2nd macropixel pair)
            int dstIdx = 0;
            for (int y = 0; y < PreviewHeight; y++)
            {
                int srcRow = (y * 4) * Width * 2;
                for (int x = 0; x < PreviewWidth; x++)
                {
                    int srcX = x * 4;
                    int macroIdx = srcRow + (srcX & ~1) * 2;
                    int u = uyvy[macroIdx] - 128;
                    int yVal = ((srcX & 1) == 0 ? uyvy[macroIdx + 1] : uyvy[macroIdx + 3]) - 16;
                    int v = uyvy[macroIdx + 2] - 128;

                    int c = 298 * yVal;
                    int r = (c + 409 * v + 128) >> 8;
                    int g = (c - 100 * u - 208 * v + 128) >> 8;
                    int b = (c + 516 * u + 128) >> 8;

                    bgra480[dstIdx++] = (byte)Math.Clamp(b, 0, 255);
                    bgra480[dstIdx++] = (byte)Math.Clamp(g, 0, 255);
                    bgra480[dstIdx++] = (byte)Math.Clamp(r, 0, 255);
                    bgra480[dstIdx++] = 255;
                }
            }
        }

        public byte[]? GetLatestPreviewPixels()
        {
            if (!_hasFrame) return null;
            lock (_frameLock)
            {
                byte[] copy = new byte[_previewBgra.Length];
                Buffer.BlockCopy(_previewBgra, 0, copy, 0, copy.Length);
                return copy;
            }
        }

        public byte[]? GetLatestPgmPixels()
        {
            if (!_hasFrame) return null;
            lock (_frameLock)
            {
                int srcIdx = 0;
                int dstIdx = 0;
                for (int i = 0; i < Width * Height; i += 2)
                {
                    int u = _uyvyManaged[srcIdx++] - 128;
                    int y0 = _uyvyManaged[srcIdx++] - 16;
                    int v = _uyvyManaged[srcIdx++] - 128;
                    int y1 = _uyvyManaged[srcIdx++] - 16;

                    int c0 = 298 * y0;
                    _pgmBgra[dstIdx++] = (byte)Math.Clamp((c0 + 516 * u + 128) >> 8, 0, 255);
                    _pgmBgra[dstIdx++] = (byte)Math.Clamp((c0 - 100 * u - 208 * v + 128) >> 8, 0, 255);
                    _pgmBgra[dstIdx++] = (byte)Math.Clamp((c0 + 409 * v + 128) >> 8, 0, 255);
                    _pgmBgra[dstIdx++] = 255;

                    int c1 = 298 * y1;
                    _pgmBgra[dstIdx++] = (byte)Math.Clamp((c1 + 516 * u + 128) >> 8, 0, 255);
                    _pgmBgra[dstIdx++] = (byte)Math.Clamp((c1 - 100 * u - 208 * v + 128) >> 8, 0, 255);
                    _pgmBgra[dstIdx++] = (byte)Math.Clamp((c1 + 409 * v + 128) >> 8, 0, 255);
                    _pgmBgra[dstIdx++] = 255;
                }

                byte[] copy = new byte[_pgmBgra.Length];
                Buffer.BlockCopy(_pgmBgra, 0, copy, 0, copy.Length);
                return copy;
            }
        }

        public void Dispose()
        {
            Stop();
            Marshal.FreeCoTaskMem(_videoBuffer);
        }
    }
}
