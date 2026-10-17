using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    public class NdiSender
    {
        private IntPtr _ndiSender = IntPtr.Zero;
        private IntPtr _namePtr = IntPtr.Zero;
        private static bool _ndiInitialized = false;
        private static readonly object _initLock = new object();

        private BlockingCollection<byte[]> _videoQueue = new BlockingCollection<byte[]>(15);
        private BlockingCollection<byte[]> _audioQueue = new BlockingCollection<byte[]>(50);

        private CancellationTokenSource? _cts;
        private Task? _videoTask;
        private Task? _audioTask;
        private Task? _feedTask;

        public bool IsRunning { get; private set; }
        public string StreamName { get; private set; } = "DeckLink Switcher - Program";
        public Action<string>? OnLog;

        public int Width = 1920;
        public int Height = 1080;
        public int FramerateN = 25000;
        public int FramerateD = 1000;

        private volatile bool _hasRealFrames = false;
        private long _lastVideoPushTick = 0;

        public bool Start(string streamName, int width = 1920, int height = 1080, double fps = 25)
        {
            if (IsRunning) return true;

            try
            {
                lock (_initLock)
                {
                    if (!_ndiInitialized)
                    {
                        if (!NdiInterop.NDIlib_initialize())
                        {
                            OnLog?.Invoke("Failed to initialize NDI runtime library.");
                            return false;
                        }
                        _ndiInitialized = true;
                        try
                        {
                            IntPtr verPtr = NdiInterop.NDIlib_version();
                            string version = Marshal.PtrToStringAnsi(verPtr) ?? "Unknown";
                            OnLog?.Invoke($"NDI initialized successfully. Version: {version}");
                        }
                        catch { }
                    }
                }

                Width = width > 0 ? width : 1920;
                Height = height > 0 ? height : 1080;
                if (fps <= 0) fps = 25;
                FramerateN = (int)Math.Round(fps * 1000);
                FramerateD = 1000;
                StreamName = string.IsNullOrWhiteSpace(streamName) ? "DeckLink Switcher - Program" : streamName;

                _hasRealFrames = false;
                _lastVideoPushTick = 0;
                _cts = new CancellationTokenSource();

                _videoQueue = new BlockingCollection<byte[]>(15);
                _audioQueue = new BlockingCollection<byte[]>(50);

                _namePtr = Marshal.StringToHGlobalAnsi(StreamName);
                var createSettings = new NdiInterop.NDIlib_send_create_t
                {
                    p_ndi_name = _namePtr,
                    p_groups = IntPtr.Zero,
                    clock_video = true,
                    clock_audio = false
                };

                _ndiSender = NdiInterop.NDIlib_send_create(ref createSettings);
                if (_ndiSender == IntPtr.Zero)
                {
                    OnLog?.Invoke("NDIlib_send_create returned null sender instance.");
                    CleanupSender();
                    return false;
                }

                IsRunning = true;

                _videoTask = Task.Run(() => VideoWorker(_cts.Token));
                _audioTask = Task.Run(() => AudioWorker(_cts.Token));
                _feedTask = Task.Run(() => FallbackFeeder(_cts.Token));

                OnLog?.Invoke($"NDI Output started: '{StreamName}' ({Width}x{Height} @ {fps:0.##} fps)");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Error starting NDI Output: {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning && _ndiSender == IntPtr.Zero) return;

            IsRunning = false;
            _cts?.Cancel();

            try { _videoTask?.Wait(500); } catch { }
            try { _audioTask?.Wait(500); } catch { }
            try { _feedTask?.Wait(500); } catch { }

            CleanupSender();

            while (_videoQueue.TryTake(out _)) { }
            while (_audioQueue.TryTake(out _)) { }

            OnLog?.Invoke("NDI Output stopped.");
        }

        private void CleanupSender()
        {
            if (_ndiSender != IntPtr.Zero)
            {
                try { NdiInterop.NDIlib_send_destroy(_ndiSender); } catch { }
                _ndiSender = IntPtr.Zero;
            }

            if (_namePtr != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(_namePtr); } catch { }
                _namePtr = IntPtr.Zero;
            }
        }

        public void PushFrame(byte[]? audio, byte[]? video)
        {
            if (!IsRunning || _ndiSender == IntPtr.Zero) return;

            if (video != null)
            {
                _hasRealFrames = true;
                Interlocked.Exchange(ref _lastVideoPushTick, Environment.TickCount64);
            }

            // Drop both if either queue is congested to keep AV sync tight
            if ((video != null && _videoQueue.Count >= _videoQueue.BoundedCapacity - 2) ||
                (audio != null && _audioQueue.Count >= _audioQueue.BoundedCapacity - 2))
            {
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

        private void VideoWorker(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning && _ndiSender != IntPtr.Zero)
            {
                try
                {
                    byte[] frame = _videoQueue.Take(token);
                    if (_ndiSender == IntPtr.Zero) break;

                    unsafe
                    {
                        fixed (byte* pVid = frame)
                        {
                            var vf = new NdiInterop.NDIlib_video_frame_v2_t
                            {
                                xres = Width,
                                yres = Height,
                                FourCC = NdiInterop.FourCC_UYVY,
                                frame_rate_N = FramerateN,
                                frame_rate_D = FramerateD,
                                picture_aspect_ratio = 16.0f / 9.0f,
                                frame_format_type = 1, // Progressive
                                timecode = NdiInterop.TimecodeSynthesize,
                                p_data = (IntPtr)pVid,
                                line_stride_in_bytes = Width * 2,
                                p_metadata = IntPtr.Zero,
                                timestamp = 0
                            };
                            NdiInterop.NDIlib_send_send_video_v2(_ndiSender, ref vf);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"NDI VideoWorker exception: {ex.Message}");
                }
            }
        }

        private void AudioWorker(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning && _ndiSender != IntPtr.Zero)
            {
                try
                {
                    byte[] audioBytes = _audioQueue.Take(token);
                    if (_ndiSender == IntPtr.Zero) break;

                    // 16-bit stereo: 4 bytes per sample frame (2ch * 2 bytes)
                    int sampleCount = audioBytes.Length / 4;
                    if (sampleCount <= 0) continue;

                    unsafe
                    {
                        fixed (byte* pAud = audioBytes)
                        {
                            var af = new NdiInterop.NDIlib_audio_frame_interleaved_16s_t
                            {
                                sample_rate = 48000,
                                no_channels = 2,
                                no_samples = sampleCount,
                                timecode = NdiInterop.TimecodeSynthesize,
                                reference_level = 0,
                                p_data = (IntPtr)pAud
                            };
                            NdiInterop.NDIlib_util_send_send_audio_interleaved_16s(_ndiSender, ref af);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"NDI AudioWorker exception: {ex.Message}");
                }
            }
        }

        private void FallbackFeeder(CancellationToken token)
        {
            int fps = FramerateN / (FramerateD > 0 ? FramerateD : 1000);
            if (fps <= 0) fps = 25;
            int frameMs = 1000 / fps;
            int audioSamplesPerFrame = 48000 / fps;
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

            byte[] silentAudio = new byte[audioSamplesPerFrame * 4];

            while (!token.IsCancellationRequested && IsRunning)
            {
                long now = Environment.TickCount64;
                bool realFrameRecent = _hasRealFrames && (now - Interlocked.Read(ref _lastVideoPushTick)) < 500;

                if (!realFrameRecent)
                {
                    if (_videoQueue.Count < 3)
                    {
                        try { _videoQueue.TryAdd((byte[])blackFrame.Clone(), 0); } catch { }
                    }
                    if (_audioQueue.Count < 5)
                    {
                        try { _audioQueue.TryAdd((byte[])silentAudio.Clone(), 0); } catch { }
                    }
                }

                Thread.Sleep(frameMs);
            }
        }
    }
}
