using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DecklinkSwitcher
{
    /// <summary>
    /// Receives an NDI stream as an alpha-blended CG overlay for Program output.
    /// Extracts BGRA/RGBA with alpha channel, prepares bounding box YUVA buffers,
    /// and provides a preview bitmap for the dedicated preview window.
    /// </summary>
    public class NdiOverlaySource : IDisposable
    {
        private const int Width = 1920;
        private const int Height = 1080;

        private CancellationTokenSource? _cts;
        private Task? _task;
        private readonly byte[] _previewBgra = new byte[480 * 270 * 4];
        private int _lastPreviewTicks = 0;

        public Action<byte[]>? OnPreviewBufferUpdated;
        public Action<string, bool, bool>? OnStatusChanged; // (statusText, isLive, isError)
        public Action<string>? OnLog;
        internal Action<PreparedHtmlOverlayFrame?>? OnOverlayFrameUpdated;

        public bool IsRunning { get; private set; } = false;
        public string CurrentSource { get; private set; } = "";

        public void Start(string name, string url = "")
        {
            Stop();
            if (string.IsNullOrWhiteSpace(name))
            {
                OnStatusChanged?.Invoke("No NDI source selected", false, true);
                return;
            }

            CurrentSource = name;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsRunning = true;
            OnStatusChanged?.Invoke("Connecting...", false, false);
            _task = Task.Run(() => ReceiveLoop(name, url, token));
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
            bool wasRunning = IsRunning;
            IsRunning = false;
            OnOverlayFrameUpdated?.Invoke(null);
            if (wasRunning) OnStatusChanged?.Invoke("OFFLINE", false, false);
        }

        private void ReceiveLoop(string name, string url, CancellationToken token)
        {
            IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
            IntPtr urlPtr = string.IsNullOrEmpty(url) ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(url);
            IntPtr recvNamePtr = Marshal.StringToHGlobalAnsi("DeckLink Switcher NDI CG");
            IntPtr recv = IntPtr.Zero;

            try
            {
                var create = new NdiInterop.NDIlib_recv_create_v3_t
                {
                    source_to_connect_to = new NdiInterop.NDIlib_source_t { p_ndi_name = namePtr, p_url_address = urlPtr },
                    // Request BGRX_BGRA so NDI delivers full 32-bit alpha channel for overlays/graphics
                    color_format = NdiInterop.RecvColorFormat_BGRX_BGRA,
                    bandwidth = NdiInterop.RecvBandwidth_Highest,
                    allow_video_fields = false,
                    p_ndi_recv_name = recvNamePtr
                };
                recv = NdiInterop.NDIlib_recv_create_v3(ref create);
                if (recv == IntPtr.Zero)
                {
                    OnStatusChanged?.Invoke("Connect failed", false, true);
                    IsRunning = false;
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
                        try { ProcessVideoFrame(video); }
                        catch (Exception ex) { OnLog?.Invoke("NDI CG frame error: " + ex.Message); }
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
                        // Overlays usually don't carry audio, free immediately
                        NdiInterop.NDIlib_recv_free_audio_v2(recv, ref audio);
                    }
                    else if (live && Environment.TickCount64 - lastVideoTick > 3000)
                    {
                        live = false;
                        OnOverlayFrameUpdated?.Invoke(null);
                        OnStatusChanged?.Invoke("No signal", false, false);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke("NDI CG receive loop error: " + ex.Message);
                OnStatusChanged?.Invoke("Error", false, true);
            }
            finally
            {
                if (recv != IntPtr.Zero) { try { NdiInterop.NDIlib_recv_destroy(recv); } catch { } }
                Marshal.FreeHGlobal(namePtr);
                if (urlPtr != IntPtr.Zero) Marshal.FreeHGlobal(urlPtr);
                Marshal.FreeHGlobal(recvNamePtr);
                IsRunning = false;
                OnOverlayFrameUpdated?.Invoke(null);
            }
        }

        private unsafe void ProcessVideoFrame(NdiInterop.NDIlib_video_frame_v2_t v)
        {
            if (v.p_data == IntPtr.Zero || v.xres <= 0 || v.yres <= 0) return;

            int sw = v.xres, sh = v.yres, stride = v.line_stride_in_bytes;
            byte* src = (byte*)v.p_data;

            // Find bounding box of non-transparent content
            int minX = sw, minY = sh, maxX = -1, maxY = -1;
            bool hasAlpha = false;

            for (int y = 0; y < sh; y++)
            {
                byte* row = src + (long)y * stride;
                for (int x = 0; x < sw; x++)
                {
                    byte a = row[x * 4 + 3];
                    if (a > 0)
                    {
                        hasAlpha = true;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (!hasAlpha || maxX < minX || maxY < minY)
            {
                OnOverlayFrameUpdated?.Invoke(null);
            }
            else
            {
                // Align minX to even boundary for UYVY macropixels
                minX &= ~1;
                if ((maxX & 1) == 0) maxX++;
                if (maxX >= sw) maxX = sw - 1;

                // Scale bounding box coordinates to 1920x1080 if input is not 1080p
                int targetMinX = sw == Width ? minX : (int)((long)minX * Width / sw);
                int targetMaxX = sw == Width ? maxX : (int)((long)maxX * Width / sw);
                int targetMinY = sh == Height ? minY : (int)((long)minY * Height / sh);
                int targetMaxY = sh == Height ? maxY : (int)((long)maxY * Height / sh);

                targetMinX &= ~1;
                if ((targetMaxX & 1) == 0) targetMaxX++;
                if (targetMaxX >= Width) targetMaxX = Width - 1;
                if (targetMaxY >= Height) targetMaxY = Height - 1;

                int boxW = targetMaxX - targetMinX + 1;
                int boxH = targetMaxY - targetMinY + 1;
                int total = boxW * boxH;

                byte[] yArr = new byte[total];
                byte[] uArr = new byte[total];
                byte[] vArr = new byte[total];
                byte[] aArr = new byte[total];

                for (int by = 0; by < boxH; by++)
                {
                    int sy = sh == Height ? (targetMinY + by) : (int)((long)(targetMinY + by) * sh / Height);
                    if (sy >= sh) sy = sh - 1;
                    byte* srow = src + (long)sy * stride;
                    int rowOffset = by * boxW;

                    for (int bx = 0; bx < boxW; bx++)
                    {
                        int sx = sw == Width ? (targetMinX + bx) : (int)((long)(targetMinX + bx) * sw / Width);
                        if (sx >= sw) sx = sw - 1;
                        byte* p = srow + sx * 4;

                        byte b = p[0];
                        byte g = p[1];
                        byte r = p[2];
                        byte a = p[3];

                        if (a == 0) continue;

                        int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                        int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

                        if (yVal < 16) yVal = 16; else if (yVal > 235) yVal = 235;
                        if (uVal < 16) uVal = 16; else if (uVal > 240) uVal = 240;
                        if (vVal < 16) vVal = 16; else if (vVal > 240) vVal = 240;

                        int idx = rowOffset + bx;
                        yArr[idx] = (byte)yVal;
                        uArr[idx] = (byte)uVal;
                        vArr[idx] = (byte)vVal;
                        aArr[idx] = a;
                    }
                }

                OnOverlayFrameUpdated?.Invoke(new PreparedHtmlOverlayFrame
                {
                    StartX = targetMinX,
                    StartY = targetMinY,
                    Width = boxW,
                    Height = boxH,
                    Y = yArr,
                    U = uArr,
                    V = vArr,
                    A = aArr
                });
            }

            // Downsample to 480x270 preview for UI dedicated preview window
            if (Environment.TickCount - _lastPreviewTicks > 33)
            {
                _lastPreviewTicks = Environment.TickCount;
                fixed (byte* dstBase = _previewBgra)
                {
                    for (int py = 0; py < 270; py++)
                    {
                        int sy = (int)((long)py * sh / 270);
                        byte* srow = src + (long)sy * stride;
                        byte* drow = dstBase + py * 480 * 4;

                        for (int px = 0; px < 480; px++)
                        {
                            int sx = (int)((long)px * sw / 480);
                            byte* sp = srow + sx * 4;
                            byte* dp = drow + px * 4;
                            dp[0] = sp[0];
                            dp[1] = sp[1];
                            dp[2] = sp[2];
                            dp[3] = sp[3];
                        }
                    }
                }
                OnPreviewBufferUpdated?.Invoke((byte[])_previewBgra.Clone());
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

