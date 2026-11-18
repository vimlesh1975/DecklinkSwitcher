using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using YoutubeExplode;
using YoutubeExplode.Videos;

namespace DecklinkSwitcher
{
    public class YouTubeSource : IDisposable
    {
        private LibVLC _libVlc;
        private MediaPlayer _mediaPlayer;
        private IntPtr _videoBuffer;
        private IntPtr _previewBuffer;
        private IntPtr _pgm1080Buffer;
        private int _width = 1920;
        private int _height = 1080;

        public Action<byte[]>? OnPreviewBufferUpdated;
        public Action<IntPtr, int>? OnUyvy1080Frame;
        public string AudioChannelName { get; set; } = "YouTube";
        public Action<double, double>? OnAudioLevelArrived;
        public Action<string, bool, bool>? OnStatusChanged; // (statusText, isLive, isError)

        public float AudioLevel { get; set; } = 1.0f;
        public bool IsConnecting { get; private set; } = false;
        public bool IsStreaming { get; private set; } = false;
        public string CurrentUrl { get; private set; } = "";

        private readonly struct TimedAudioPacket
        {
            public readonly long PtsUs;
            public readonly short[] Samples;
            public TimedAudioPacket(long ptsUs, short[] samples)
            {
                PtsUs = ptsUs;
                Samples = samples;
            }
        }

        private readonly System.Collections.Generic.Queue<TimedAudioPacket> _ptsQueue = new();
        private readonly object _delayLock = new();
        private System.Threading.CancellationTokenSource? _audioPacerCts;
        private volatile bool _isAudioPaused = false;

        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        private static extern long libvlc_clock();

        private static long GetLibVlcClockUs()
        {
            try { return libvlc_clock(); }
            catch { return System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L / System.Diagnostics.Stopwatch.Frequency; }
        }

        private MediaPlayer.LibVLCVideoLockCb _lockVideoCb;
        private MediaPlayer.LibVLCVideoUnlockCb _unlockVideoCb;
        private MediaPlayer.LibVLCVideoDisplayCb _displayVideoCb;

        private MediaPlayer.LibVLCAudioPlayCb _playAudioCb;
        private MediaPlayer.LibVLCAudioPauseCb _pauseAudioCb;
        private MediaPlayer.LibVLCAudioResumeCb _resumeAudioCb;
        private MediaPlayer.LibVLCAudioFlushCb _flushAudioCb;
        private MediaPlayer.LibVLCAudioDrainCb _drainAudioCb;

        private int _lastPreviewTicks = 0;

        public YouTubeSource()
        {
            _videoBuffer = Marshal.AllocCoTaskMem(_width * _height * 2); // 1080p UYVY
            _previewBuffer = Marshal.AllocCoTaskMem(480 * 270 * 4);       // 480x270 BGRA (UI preview)
            _pgm1080Buffer = Marshal.AllocCoTaskMem(_width * _height * 4); // 1080p BGRA (PGM broadcast)

            Core.Initialize();
            _libVlc = new LibVLC(
                "--preferred-resolution=1080",
                "--adaptive-logic=highest",
                "--adaptive-maxwidth=1920",
                "--adaptive-maxheight=1080"
            );
            _mediaPlayer = new MediaPlayer(_libVlc);

            _mediaPlayer.SetVideoFormat("UYVY", (uint)_width, (uint)_height, (uint)(_width * 2));

            _lockVideoCb = new MediaPlayer.LibVLCVideoLockCb(LockVideo);
            _unlockVideoCb = new MediaPlayer.LibVLCVideoUnlockCb(UnlockVideo);
            _displayVideoCb = new MediaPlayer.LibVLCVideoDisplayCb(DisplayVideo);
            _mediaPlayer.SetVideoCallbacks(_lockVideoCb, _unlockVideoCb, _displayVideoCb);

            // S16N (Signed 16-bit PCM), 48000 Hz, 2 Channels
            _mediaPlayer.SetAudioFormat("S16N", 48000, 2);

            _playAudioCb = new MediaPlayer.LibVLCAudioPlayCb(PlayAudio);
            _pauseAudioCb = new MediaPlayer.LibVLCAudioPauseCb(PauseAudio);
            _resumeAudioCb = new MediaPlayer.LibVLCAudioResumeCb(ResumeAudio);
            _flushAudioCb = new MediaPlayer.LibVLCAudioFlushCb(FlushAudio);
            _drainAudioCb = new MediaPlayer.LibVLCAudioDrainCb(DrainAudio);
            _mediaPlayer.SetAudioCallbacks(_playAudioCb, _pauseAudioCb, _resumeAudioCb, _flushAudioCb, _drainAudioCb);

            _mediaPlayer.Buffering += (s, e) =>
            {
                if (e.Cache < 100)
                {
                    OnStatusChanged?.Invoke($"Buffering {e.Cache:F0}%", true, false);
                }
                else
                {
                    OnStatusChanged?.Invoke("LIVE", true, false);
                }
            };

            _mediaPlayer.EncounteredError += (s, e) =>
            {
                IsStreaming = false;
                IsConnecting = false;
                OnStatusChanged?.Invoke("Stream Error", false, true);
            };

            _mediaPlayer.EndReached += (s, e) =>
            {
                IsStreaming = false;
                IsConnecting = false;
                OnStatusChanged?.Invoke("Stream Ended", false, false);
            };
        }

        public async Task StartAsync(string inputUrl)
        {
            Stop();
            inputUrl = inputUrl?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(inputUrl))
            {
                OnStatusChanged?.Invoke("No URL provided", false, true);
                return;
            }

            CurrentUrl = inputUrl;
            IsConnecting = true;
            OnStatusChanged?.Invoke("Resolving stream...", false, false);

            string streamUrl = inputUrl;
            try
            {
                bool isDirectHls = inputUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                                  inputUrl.Contains(".m3u8?", StringComparison.OrdinalIgnoreCase);

                if (!isDirectHls && (inputUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                                     inputUrl.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
                {
                    var videoId = VideoId.TryParse(inputUrl);
                    if (videoId == null)
                    {
                        OnStatusChanged?.Invoke("Invalid YouTube URL", false, true);
                        IsConnecting = false;
                        return;
                    }

                    var ytClient = new YoutubeClient();
                    streamUrl = await ytClient.Videos.Streams.GetHttpLiveStreamUrlAsync(videoId.Value);
                    if (string.IsNullOrEmpty(streamUrl))
                    {
                        OnStatusChanged?.Invoke("Not a live stream or offline", false, true);
                        IsConnecting = false;
                        return;
                    }
                }

            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("Resolve error: " + ex.Message, false, true);
                IsConnecting = false;
                return;
            }

            OnStatusChanged?.Invoke("Connecting...", false, false);

            try
            {
                var media = new Media(_libVlc, streamUrl, FromType.FromLocation);
                // Request highest resolution (1080p) while preserving demuxed audio tracks
                media.AddOption(":preferred-resolution=1080");
                media.AddOption(":adaptive-logic=highest");
                media.AddOption(":adaptive-maxwidth=1920");
                media.AddOption(":adaptive-maxheight=1080");
                media.AddOption(":network-caching=1500");
                media.AddOption(":live-caching=1500");

                _mediaPlayer.Play(media);
                IsStreaming = true;
                IsConnecting = false;
                _isAudioPaused = false;
                StartAudioPacer();
                OnStatusChanged?.Invoke("LIVE", true, false);
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("Play error: " + ex.Message, false, true);
                IsConnecting = false;
                IsStreaming = false;
            }
        }

        public void Stop()
        {
            StopAudioPacer();
            try
            {
                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Stop();
                }
            }
            catch { }

            lock (_delayLock)
            {
                _ptsQueue.Clear();
            }

            IsStreaming = false;
            IsConnecting = false;
            OnStatusChanged?.Invoke("OFFLINE", false, false);
        }

        private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _videoBuffer);
            return _videoBuffer;
        }

        private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
        }

        private void DisplayVideo(IntPtr opaque, IntPtr picture)
        {
            // 1. Maintain full-resolution 1080p UYVY broadcast buffer directly from decoder
            if (OnUyvy1080Frame != null)
            {
                OnUyvy1080Frame(_videoBuffer, _width * _height * 2);
            }
            else
            {
                lock (MainWindow._pgmLock)
                {
                    if (MainWindow.LatestYouTubeUyvy == null || MainWindow.LatestYouTubeUyvy.Length != _width * _height * 2)
                        MainWindow.LatestYouTubeUyvy = new byte[_width * _height * 2];
                    Marshal.Copy(_videoBuffer, MainWindow.LatestYouTubeUyvy, 0, _width * _height * 2);
                }
            }

            // 2. Convert to full-resolution 1080p BGRA for PGM streaming consumers
            if (MainWindow.ActiveSourceType == 6)
            {
                unsafe
                {
                    DeckLinkDevice.ConvertUYVYToBGRA_Downsampled(_videoBuffer, _width, _height, _width * 2, _pgm1080Buffer, _width, _height, _width * 4);
                }
                lock (MainWindow._pgmLock)
                {
                    if (MainWindow._latestPgmFrame == null || MainWindow._latestPgmFrame.Length != _width * _height * 4)
                        MainWindow._latestPgmFrame = new byte[_width * _height * 4];
                    Marshal.Copy(_pgm1080Buffer, MainWindow._latestPgmFrame, 0, _width * _height * 4);
                }
            }

            // 3. Downsample to 480x270 BGRA for UI preview thumbnails
            if (Environment.TickCount - _lastPreviewTicks > 33)
            {
                _lastPreviewTicks = Environment.TickCount;
                unsafe
                {
                    DeckLinkDevice.ConvertUYVYToBGRA_Downsampled(_videoBuffer, _width, _height, _width * 2, _previewBuffer, 480, 270, 480 * 4);
                }
                byte[] previewCopy = new byte[480 * 270 * 4];
                Marshal.Copy(_previewBuffer, previewCopy, 0, previewCopy.Length);
                OnPreviewBufferUpdated?.Invoke(previewCopy);
            }
        }

        public byte[]? GetLatestPgmPixels()
        {
            if (_videoBuffer == IntPtr.Zero || !IsStreaming) return null;
            byte[] copy = new byte[_width * _height * 4];
            unsafe
            {
                fixed (byte* dst = copy)
                {
                    DeckLinkDevice.ConvertUYVYToBGRA_Downsampled(_videoBuffer, _width, _height, _width * 2, (IntPtr)dst, _width, _height, _width * 4);
                }
            }
            return copy;
        }

        public byte[]? GetLatestPreviewPixels()
        {
            if (_previewBuffer == IntPtr.Zero || !IsStreaming) return null;
            byte[] copy = new byte[480 * 270 * 4];
            Marshal.Copy(_previewBuffer, copy, 0, copy.Length);
            return copy;
        }

        private void StartAudioPacer()
        {
            StopAudioPacer();
            var cts = new System.Threading.CancellationTokenSource();
            _audioPacerCts = cts;
            var token = cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_isAudioPaused)
                        {
                            long nowUs = GetLibVlcClockUs();
                            // Release packets whose presentation timestamp is due within 15ms
                            long releaseHorizonUs = nowUs + 15_000L;

                            while (true)
                            {
                                short[]? emit = null;
                                lock (_delayLock)
                                {
                                    if (_ptsQueue.Count > 0 && _ptsQueue.Peek().PtsUs <= releaseHorizonUs)
                                    {
                                        emit = _ptsQueue.Dequeue().Samples;
                                    }
                                }
                                if (emit == null) break;
                                DecklinkSwitcher.MainWindow.PushAudioPacket(AudioChannelName, emit);
                                UpdateAudioMeter(emit);
                            }
                        }
                        await Task.Delay(10, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, token);
        }

        private void StopAudioPacer()
        {
            try { _audioPacerCts?.Cancel(); } catch { }
            try { _audioPacerCts?.Dispose(); } catch { }
            _audioPacerCts = null;
        }

        private void PlayAudio(IntPtr data, IntPtr samples, uint count, long pts)
        {
            if (count == 0 || samples == IntPtr.Zero) return;

            int totalShorts = (int)count * 2;
            short[] packet = new short[totalShorts];
            Marshal.Copy(samples, packet, 0, totalShorts);

            long nowUs = GetLibVlcClockUs();
            // If pts is already due or clock is unavailable, fall back cleanly
            if (pts <= 0 || pts < nowUs - 5_000_000L)
            {
                pts = nowUs;
            }

            lock (_delayLock)
            {
                _ptsQueue.Enqueue(new TimedAudioPacket(pts, packet));
                // Cap queue to prevent unbounded memory growth if paused/stalled (>10s)
                while (_ptsQueue.Count > 500)
                {
                    _ptsQueue.Dequeue();
                }
            }
        }

        private void UpdateAudioMeter(short[] packet)
        {
            int maxL = 0;
            int maxR = 0;
            for (int i = 0; i < packet.Length; i += 2)
            {
                int l = Math.Abs((int)packet[i]);
                int r = Math.Abs((int)packet[i + 1]);
                if (l > maxL) maxL = l;
                if (r > maxR) maxR = r;
            }
            double volL = (maxL / 32768.0) * 100.0;
            double volR = (maxR / 32768.0) * 100.0;
            OnAudioLevelArrived?.Invoke(volL, volR);
        }

        private void PauseAudio(IntPtr data, long pts) { _isAudioPaused = true; }
        private void ResumeAudio(IntPtr data, long pts) { _isAudioPaused = false; }
        private void FlushAudio(IntPtr data, long pts)
        {
            lock (_delayLock) { _ptsQueue.Clear(); }
        }
        private void DrainAudio(IntPtr data) { }

        public void Dispose()
        {
            Stop();
            _mediaPlayer.Dispose();
            _libVlc.Dispose();
            Marshal.FreeCoTaskMem(_videoBuffer);
            Marshal.FreeCoTaskMem(_previewBuffer);
            Marshal.FreeCoTaskMem(_pgm1080Buffer);
        }
    }
}
