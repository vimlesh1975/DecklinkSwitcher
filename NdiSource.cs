using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    public class NdiSourceInfo
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public override string ToString() => Name;
    }

    /// <summary>
    /// Receives an NDI source (video + audio) and feeds it into the switcher as a regular source.
    /// Video is delivered as a 1920x1080 UYVY frame in MainWindow.LatestNdiUyvy, audio is pushed to
    /// the mixer under the name "NDI".
    /// </summary>
    public class NdiSource : IDisposable
    {
        private const int Width = 1920;
        private const int Height = 1080;

        private static bool _ndiInitialized = false;
        private static readonly object _initLock = new object();

        private CancellationTokenSource? _cts;
        private Task? _task;
        private readonly byte[] _frameBuffer = new byte[Width * Height * 2];
        private readonly byte[] _previewBgra = new byte[480 * 270 * 4];
        private int _lastPreviewTicks = 0;

        public Action<byte[]>? OnPreviewBufferUpdated;
        public Action<double, double>? OnAudioLevelArrived;
        public Action<string, bool, bool>? OnStatusChanged; // (statusText, isLive, isError)
        public Action<string>? OnLog;

        public float AudioLevel { get; set; } = 1.0f;
        public bool IsStreaming { get; private set; } = false;
        public string CurrentSource { get; private set; } = "";

        private static bool EnsureInitialized(Action<string>? log)
        {
            lock (_initLock)
            {
                if (_ndiInitialized) return true;
                try
                {
                    if (!NdiInterop.NDIlib_initialize())
                    {
                        log?.Invoke("Failed to initialize NDI runtime library.");
                        return false;
                    }
                    _ndiInitialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    log?.Invoke("NDI runtime not available: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>Discovers NDI sources on the network (blocking, call from a background thread).</summary>
        public static List<NdiSourceInfo> FindSources(int waitMs = 2000, Action<string>? log = null)
        {
            var result = new List<NdiSourceInfo>();
            if (!EnsureInitialized(log)) return result;

            IntPtr finder = IntPtr.Zero;
            try
            {
                var settings = new NdiInterop.NDIlib_find_create_t { show_local_sources = true, p_groups = IntPtr.Zero, p_extra_ips = IntPtr.Zero };
                finder = NdiInterop.NDIlib_find_create_v2(ref settings);
                if (finder == IntPtr.Zero) return result;

                // Sources are discovered asynchronously; give the network a moment to answer.
                NdiInterop.NDIlib_find_wait_for_sources(finder, (uint)waitMs);
                NdiInterop.NDIlib_find_wait_for_sources(finder, 500);

                IntPtr list = NdiInterop.NDIlib_find_get_current_sources(finder, out uint count);
                int size = Marshal.SizeOf<NdiInterop.NDIlib_source_t>();
                for (uint i = 0; i < count && list != IntPtr.Zero; i++)
                {
                    var src = Marshal.PtrToStructure<NdiInterop.NDIlib_source_t>(list + (int)(i * size));
                    string name = Marshal.PtrToStringAnsi(src.p_ndi_name) ?? "";
                    string url = src.p_url_address != IntPtr.Zero ? (Marshal.PtrToStringAnsi(src.p_url_address) ?? "") : "";
                    if (!string.IsNullOrEmpty(name)) result.Add(new NdiSourceInfo { Name = name, Url = url });
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("NDI find error: " + ex.Message);
            }
            finally
            {
                if (finder != IntPtr.Zero) { try { NdiInterop.NDIlib_find_destroy(finder); } catch { } }
            }
            return result;
        }

        public void Start(NdiSourceInfo source)
        {
            Stop();
            if (source == null || string.IsNullOrWhiteSpace(source.Name))
            {
                OnStatusChanged?.Invoke("No NDI source selected", false, true);
                return;
            }
            if (!EnsureInitialized(OnLog))
            {
                OnStatusChanged?.Invoke("NDI runtime missing", false, true);
                return;
            }

            CurrentSource = source.Name;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsStreaming = true;
            OnStatusChanged?.Invoke("Connecting...", false, false);
            _task = Task.Run(() => ReceiveLoop(source.Name, source.Url, token));
        }

        public void Stop()
        {
            var cts = _cts;
            var task = _task;
            _cts = null;
            _task = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { task?.Wait(1500); } catch { }
                cts.Dispose();
            }
            bool wasStreaming = IsStreaming;
            IsStreaming = false;
            if (wasStreaming) OnStatusChanged?.Invoke("OFFLINE", false, false);
        }

        private void ReceiveLoop(string name, string url, CancellationToken token)
        {
            IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
            IntPtr urlPtr = string.IsNullOrEmpty(url) ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(url);
            IntPtr recvNamePtr = Marshal.StringToHGlobalAnsi("DeckLink Switcher");
            IntPtr recv = IntPtr.Zero;

            try
            {
                var create = new NdiInterop.NDIlib_recv_create_v3_t
                {
                    source_to_connect_to = new NdiInterop.NDIlib_source_t { p_ndi_name = namePtr, p_url_address = urlPtr },
                    color_format = NdiInterop.RecvColorFormat_UYVY_BGRA,
                    bandwidth = NdiInterop.RecvBandwidth_Highest,
                    allow_video_fields = false,
                    p_ndi_recv_name = recvNamePtr
                };
                recv = NdiInterop.NDIlib_recv_create_v3(ref create);
                if (recv == IntPtr.Zero)
                {
                    OnStatusChanged?.Invoke("NDI connect failed", false, true);
                    IsStreaming = false;
                    return;
                }

                long lastVideoTick = Environment.TickCount64;
                bool live = false;

                while (!token.IsCancellationRequested)
                {
                    var video = new NdiInterop.NDIlib_video_frame_v2_t();
                    var audio = new NdiInterop.NDIlib_audio_frame_v2_t();
                    int type = NdiInterop.NDIlib_recv_capture_v2(recv, ref video, ref audio, IntPtr.Zero, 100);

                    if (type == NdiInterop.FrameType_Video)
                    {
                        try { HandleVideo(video); }
                        catch (Exception ex) { OnLog?.Invoke("NDI video error: " + ex.Message); }
                        NdiInterop.NDIlib_recv_free_video_v2(recv, ref video);
                        lastVideoTick = Environment.TickCount64;
                        if (!live)
                        {
                            live = true;
                            OnStatusChanged?.Invoke("LIVE", true, false);
                        }
                    }
                    else if (type == NdiInterop.FrameType_Audio)
                    {
                        try { HandleAudio(audio); }
                        catch (Exception ex) { OnLog?.Invoke("NDI audio error: " + ex.Message); }
                        NdiInterop.NDIlib_recv_free_audio_v2(recv, ref audio);
                    }
                    else if (live && Environment.TickCount64 - lastVideoTick > 3000)
                    {
                        live = false;
                        OnStatusChanged?.Invoke("No signal", false, false);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("NDI receive error: " + ex.Message);
                OnStatusChanged?.Invoke("NDI error", false, true);
            }
            finally
            {
                if (recv != IntPtr.Zero) { try { NdiInterop.NDIlib_recv_destroy(recv); } catch { } }
                Marshal.FreeHGlobal(namePtr);
                if (urlPtr != IntPtr.Zero) Marshal.FreeHGlobal(urlPtr);
                Marshal.FreeHGlobal(recvNamePtr);
                IsStreaming = false;
            }
        }

        private unsafe void HandleVideo(NdiInterop.NDIlib_video_frame_v2_t v)
        {
            if (v.p_data == IntPtr.Zero || v.xres <= 0 || v.yres <= 0) return;

            int sw = v.xres, sh = v.yres, stride = v.line_stride_in_bytes;
            bool isUyvy = v.FourCC == NdiInterop.FourCC_UYVY;
            bool isBgra = v.FourCC == NdiInterop.FourCC_BGRA || v.FourCC == NdiInterop.FourCC_BGRX;
            if (!isUyvy && !isBgra) return;

            byte* src = (byte*)v.p_data;
            fixed (byte* dstBase = _frameBuffer)
            {
                for (int y = 0; y < Height; y++)
                {
                    int sy = sh == Height ? y : (int)((long)y * sh / Height);
                    byte* srow = src + (long)sy * stride;
                    byte* drow = dstBase + (long)y * Width * 2;

                    if (isUyvy)
                    {
                        if (sw == Width)
                        {
                            Buffer.MemoryCopy(srow, drow, Width * 2, Width * 2);
                        }
                        else
                        {
                            int smpCount = sw / 2;
                            for (int i = 0; i < Width / 2; i++)
                            {
                                int smp = (int)((long)i * smpCount / (Width / 2));
                                *(uint*)(drow + i * 4) = *(uint*)(srow + smp * 4);
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < Width / 2; i++)
                        {
                            int x0 = (int)((long)(2 * i) * sw / Width);
                            int x1 = (int)((long)(2 * i + 1) * sw / Width);
                            byte* p0 = srow + x0 * 4;
                            byte* p1 = srow + x1 * 4;
                            // BGRA order in memory; BT.709 limited range
                            double b0 = p0[0], g0 = p0[1], r0 = p0[2];
                            double b1 = p1[0], g1 = p1[1], r1 = p1[2];
                            double y0 = 16 + 0.1826 * r0 + 0.6142 * g0 + 0.0620 * b0;
                            double y1 = 16 + 0.1826 * r1 + 0.6142 * g1 + 0.0620 * b1;
                            double r = (r0 + r1) * 0.5, g = (g0 + g1) * 0.5, b = (b0 + b1) * 0.5;
                            double u = 128 - 0.1006 * r - 0.3386 * g + 0.4392 * b;
                            double vv = 128 + 0.4392 * r - 0.3989 * g - 0.0403 * b;
                            drow[i * 4 + 0] = Clamp(u);
                            drow[i * 4 + 1] = Clamp(y0);
                            drow[i * 4 + 2] = Clamp(vv);
                            drow[i * 4 + 3] = Clamp(y1);
                        }
                    }
                }
            }

            lock (MainWindow._pgmLock)
            {
                if (MainWindow.LatestNdiUyvy == null || MainWindow.LatestNdiUyvy.Length != _frameBuffer.Length)
                    MainWindow.LatestNdiUyvy = new byte[_frameBuffer.Length];
                Buffer.BlockCopy(_frameBuffer, 0, MainWindow.LatestNdiUyvy, 0, _frameBuffer.Length);
            }

            if (Environment.TickCount - _lastPreviewTicks > 33)
            {
                _lastPreviewTicks = Environment.TickCount;
                fixed (byte* s = _frameBuffer)
                fixed (byte* d = _previewBgra)
                {
                    DeckLinkDevice.ConvertUYVYToBGRA_Downsampled((IntPtr)s, Width, Height, Width * 2, (IntPtr)d, 480, 270, 480 * 4);
                }
                OnPreviewBufferUpdated?.Invoke((byte[])_previewBgra.Clone());
            }
        }

        private static byte Clamp(double v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

        private unsafe void HandleAudio(NdiInterop.NDIlib_audio_frame_v2_t a)
        {
            int n = a.no_samples, ch = a.no_channels, sr = a.sample_rate;
            if (a.p_data == IntPtr.Zero || n <= 0 || ch <= 0 || sr <= 0) return;

            int stride = a.channel_stride_in_bytes / 4; // floats per channel plane
            float* left = (float*)a.p_data;
            float* right = ch > 1 ? left + stride : left;

            int outN = sr == 48000 ? n : (int)((long)n * 48000 / sr);
            if (outN <= 0) return;
            short[] packet = new short[outN * 2];
            int maxL = 0, maxR = 0;

            for (int j = 0; j < outN; j++)
            {
                float l, r;
                if (sr == 48000)
                {
                    l = left[j];
                    r = right[j];
                }
                else
                {
                    double pos = j * (double)sr / 48000.0;
                    int i0 = Math.Min((int)pos, n - 1);
                    int i1 = Math.Min(i0 + 1, n - 1);
                    float frac = (float)(pos - i0);
                    l = left[i0] + (left[i1] - left[i0]) * frac;
                    r = right[i0] + (right[i1] - right[i0]) * frac;
                }
                short sl = ToShort(l);
                short sr16 = ToShort(r);
                packet[j * 2] = sl;
                packet[j * 2 + 1] = sr16;
                int al = Math.Abs((int)sl), ar = Math.Abs((int)sr16);
                if (al > maxL) maxL = al;
                if (ar > maxR) maxR = ar;
            }

            MainWindow.PushAudioPacket("NDI", packet);
            OnAudioLevelArrived?.Invoke(maxL / 32768.0 * 100.0, maxR / 32768.0 * 100.0);
        }

        private static short ToShort(float f)
        {
            float s = f * 32767f;
            if (s > 32767f) s = 32767f;
            else if (s < -32768f) s = -32768f;
            return (short)s;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

