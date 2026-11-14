using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;

namespace DecklinkSwitcher
{
    internal sealed class PreparedHtmlOverlayFrame
    {
        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Y { get; init; } = Array.Empty<byte>();
        public byte[] U { get; init; } = Array.Empty<byte>();
        public byte[] V { get; init; } = Array.Empty<byte>();
        public byte[] A { get; init; } = Array.Empty<byte>();
    }

    public class HtmlBrowserSource : IDisposable
    {
        private const int FrameWidth = 1920;
        private const int FrameHeight = 1080;
        private const int BgraFrameBytes = FrameWidth * FrameHeight * 4;
        private const int UyvyFrameBytes = FrameWidth * FrameHeight * 2;
        private const int TargetFps = 25;

        private static readonly object _cefInitLock = new object();
        private static bool _cefInitialized = false;

        private readonly bool _isTransparentOverlay;
        private readonly string _instanceName;
        private ChromiumWebBrowser? _browser;
        private volatile bool _isRunning = false;
        private volatile bool _isDisposed = false;
        private bool _isMuted = false;

        private readonly byte[] _paintCopy1080 = new byte[BgraFrameBytes];
        private readonly byte[] _work1080 = new byte[BgraFrameBytes];
        private readonly object _paintSwapLock = new object();
        private volatile bool _hasPendingPaint = false;
        private int _isProcessingFrame = 0;

        private readonly byte[] _latestPreview480 = new byte[480 * 270 * 4];
        private readonly byte[] _latestPgm1080Bgra = new byte[BgraFrameBytes];
        private readonly byte[] _latestUyvy1080 = new byte[UyvyFrameBytes];
        private readonly byte[] _deflickerUyvy1080 = new byte[UyvyFrameBytes];
        private readonly object _bufferLock = new object();
        private bool _hasCapturedFrame = false;

        public Action<byte[]>? OnPreviewBufferUpdated;
        public Action<string, bool, bool>? OnStatusChanged; // (status, isLive, isError)
        internal Action<PreparedHtmlOverlayFrame?>? OnOverlayFrameUpdated;

        public bool IsRunning => _isRunning;
        public string CurrentUrl { get; private set; } = "";

        public HtmlBrowserSource(bool isTransparentOverlay, string instanceName)
        {
            _isTransparentOverlay = isTransparentOverlay;
            _instanceName = instanceName;
            _isMuted = isTransparentOverlay;
        }

        public static string NormalizeUrl(string rawUrl)
        {
            string url = (rawUrl ?? "").Trim();
            if (string.IsNullOrEmpty(url)) return "";

            if (File.Exists(url))
            {
                return new Uri(Path.GetFullPath(url)).AbsoluteUri;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + url;
            }

            return url;
        }

        private static bool EnsureCefInitialized(string instanceName)
        {
            lock (_cefInitLock)
            {
                if (_cefInitialized || Cef.IsInitialized == true)
                {
                    _cefInitialized = true;
                    return true;
                }

                try
                {
                    bool initResult = false;
                    Action initAction = () =>
                    {
                        if (_cefInitialized || Cef.IsInitialized == true)
                        {
                            initResult = true;
                            return;
                        }

                        string cacheRoot = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "DecklinkSwitcher",
                            "CefCache");
                        Directory.CreateDirectory(cacheRoot);

                        var settings = new CefSettings
                        {
                            WindowlessRenderingEnabled = true,
                            RootCachePath = cacheRoot,
                            LogSeverity = LogSeverity.Warning,
                            LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cef_debug.log")
                        };

                        settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                        settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                        settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");
                        settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
                        settings.CefCommandLineArgs.Add("force-device-scale-factor", "1");

                        initResult = Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
                    };

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.CheckAccess())
                        dispatcher.Invoke(initAction);
                    else
                        initAction();

                    _cefInitialized = initResult;
                    if (!initResult)
                    {
                        MainWindow.Log($"[HtmlBrowserSource:{instanceName}] Cef.Initialize returned false.");
                    }
                    return initResult;
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[HtmlBrowserSource:{instanceName}] Cef.Initialize error: {ex.Message}");
                    return false;
                }
            }
        }

        public static void ShutdownCef()
        {
            lock (_cefInitLock)
            {
                try
                {
                    Action shutdownAction = () =>
                    {
                        if (Cef.IsInitialized == true)
                        {
                            Cef.Shutdown();
                        }
                    };
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.CheckAccess())
                        dispatcher.Invoke(shutdownAction);
                    else
                        shutdownAction();
                }
                catch { }
                _cefInitialized = false;
            }
        }

        private Task<bool> EnsureBrowserCreatedAsync(string initialUrl)
        {
            if (_browser != null && !_browser.IsDisposed)
            {
                return Task.FromResult(true);
            }

            if (!EnsureCefInitialized(_instanceName))
            {
                OnStatusChanged?.Invoke("CEF ERR", false, true);
                return Task.FromResult(false);
            }

            var browserSettings = new BrowserSettings
            {
                WindowlessFrameRate = TargetFps,
                BackgroundColor = _isTransparentOverlay ? 0x00000000u : 0xFF000000u
            };

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var browser = new ChromiumWebBrowser(initialUrl, browserSettings)
            {
                Size = new System.Drawing.Size(FrameWidth, FrameHeight)
            };

            browser.BrowserInitialized += (s, e) =>
            {
                try
                {
                    browser.GetBrowserHost()?.SetAudioMuted(_isMuted);
                }
                catch { }
                tcs.TrySetResult(true);
            };

            browser.LoadingStateChanged += (s, e) =>
            {
                if (!_isRunning) return;
                if (e.IsLoading)
                {
                    OnStatusChanged?.Invoke("LOADING...", false, false);
                }
            };

            browser.LoadError += (s, e) =>
            {
                if (!_isRunning || e.ErrorCode == CefErrorCode.Aborted) return;
                OnStatusChanged?.Invoke($"ERR: {e.ErrorCode}", false, true);
            };

            browser.FrameLoadEnd += async (s, e) =>
            {
                if (!_isRunning || !e.Frame.IsMain) return;
                if (_isTransparentOverlay)
                {
                    try
                    {
                        await browser.EvaluateScriptAsync(
                            "document.documentElement.style.background='transparent'; if(document.body) document.body.style.background='transparent';");
                    }
                    catch { }
                }
                OnStatusChanged?.Invoke("LIVE", true, false);
            };

            browser.Paint += OnBrowserPaint;
            _browser = browser;

            if (browser.IsBrowserInitialized)
            {
                try { browser.GetBrowserHost()?.SetAudioMuted(_isMuted); } catch { }
                tcs.TrySetResult(true);
            }

            return tcs.Task;
        }

        public async Task StartAsync(string rawUrl)
        {
            if (_isDisposed) return;

            string url = NormalizeUrl(rawUrl);
            if (string.IsNullOrEmpty(url))
            {
                OnStatusChanged?.Invoke("NO URL", false, true);
                return;
            }

            CurrentUrl = url;
            OnStatusChanged?.Invoke("LOADING...", false, false);

            bool wasCreated = _browser == null || _browser.IsDisposed;
            _isRunning = true;

            bool ready = await EnsureBrowserCreatedAsync(url);
            if (!ready || _browser == null || _browser.IsDisposed)
            {
                _isRunning = false;
                return;
            }

            try
            {
                _browser.GetBrowserHost()?.WasHidden(false);
                _browser.GetBrowserHost()?.SetAudioMuted(_isMuted);
                if (!wasCreated)
                {
                    _browser.Load(url);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Load error: {ex.Message}");
                OnStatusChanged?.Invoke("INVALID URL", false, true);
            }
        }

        public void Reload()
        {
            if (_browser == null || _browser.IsDisposed || !_browser.IsBrowserInitialized) return;
            try { _browser.Reload(); } catch { }
        }

        public void SetMuted(bool muted)
        {
            _isMuted = muted;
            if (_browser == null || _browser.IsDisposed || !_browser.IsBrowserInitialized) return;
            try { _browser.GetBrowserHost()?.SetAudioMuted(muted); } catch { }
        }

        public void Stop()
        {
            _isRunning = false;
            _hasCapturedFrame = false;
            _hasPendingPaint = false;

            if (_browser != null && !_browser.IsDisposed && _browser.IsBrowserInitialized)
            {
                try { _browser.Load("about:blank"); } catch { }
                try { _browser.GetBrowserHost()?.WasHidden(true); } catch { }
            }

            OnOverlayFrameUpdated?.Invoke(null);
            OnStatusChanged?.Invoke("OFFLINE", false, false);
        }

        private unsafe void OnBrowserPaint(object? sender, OnPaintEventArgs e)
        {
            if (!_isRunning || e.IsPopup || e.BufferHandle == IntPtr.Zero) return;
            if (e.Width != FrameWidth || e.Height != FrameHeight) return;

            lock (_paintSwapLock)
            {
                fixed (byte* dst = _paintCopy1080)
                {
                    Buffer.MemoryCopy((void*)e.BufferHandle, dst, BgraFrameBytes, BgraFrameBytes);
                }
                _hasPendingPaint = true;
            }

            if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) == 0)
            {
                Task.Run(ProcessPendingFramesLoop);
            }
        }

        private void ProcessPendingFramesLoop()
        {
            try
            {
                while (_isRunning && !_isDisposed)
                {
                    bool hasFrame;
                    lock (_paintSwapLock)
                    {
                        hasFrame = _hasPendingPaint;
                        if (hasFrame)
                        {
                            Buffer.BlockCopy(_paintCopy1080, 0, _work1080, 0, BgraFrameBytes);
                            _hasPendingPaint = false;
                        }
                    }

                    if (!hasFrame) break;
                    ProcessBgra1080Frame(_work1080);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Frame process error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessingFrame, 0);
                if (_hasPendingPaint && _isRunning && !_isDisposed &&
                    Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) == 0)
                {
                    Task.Run(ProcessPendingFramesLoop);
                }
            }
        }

        private unsafe void Downsample480x270(byte[] src1080, byte[] dst480)
        {
            // Exact 4x4 downscale (1920x1080 -> 480x270) in BGRA32
            fixed (byte* pSrc = src1080)
            fixed (byte* pDst = dst480)
            {
                const int srcStride = FrameWidth * 4;
                const int dstStride = 480 * 4;
                for (int y = 0; y < 270; y++)
                {
                    byte* sRow = pSrc + (y * 4) * srcStride;
                    byte* dRow = pDst + y * dstStride;
                    for (int x = 0; x < 480; x++)
                    {
                        byte* sPx = sRow + (x * 16);
                        byte* dPx = dRow + (x * 4);
                        dPx[0] = sPx[0];
                        dPx[1] = sPx[1];
                        dPx[2] = sPx[2];
                        dPx[3] = sPx[3];
                    }
                }
            }
        }

        private unsafe void ProcessBgra1080Frame(byte[] bgra1080)
        {
            byte[] prevCopy = new byte[480 * 270 * 4];
            Downsample480x270(bgra1080, prevCopy);

            if (_isTransparentOverlay)
            {
                fixed (byte* scan0 = bgra1080)
                {
                    const int stride = FrameWidth * 4;
                    int minX = FrameWidth, maxX = -1, minY = FrameHeight, maxY = -1;
                    for (int y = 0; y < FrameHeight; y++)
                    {
                        byte* row = scan0 + y * stride;
                        for (int x = 0; x < FrameWidth; x++)
                        {
                            if (row[x * 4 + 3] > 0)
                            {
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                        }
                    }

                    if (maxX < minX || maxY < minY)
                    {
                        OnOverlayFrameUpdated?.Invoke(null);
                    }
                    else
                    {
                        minX &= ~1;
                        if ((maxX & 1) == 0) maxX++;
                        if (maxX >= FrameWidth) maxX = FrameWidth - 1;
                        if (minY > 0) minY--;
                        if (maxY < FrameHeight - 1) maxY++;

                        int boxW = maxX - minX + 1;
                        int boxH = maxY - minY + 1;
                        int total = boxW * boxH;

                        byte[] yArr = new byte[total];
                        byte[] uArr = new byte[total];
                        byte[] vArr = new byte[total];
                        byte[] aArr = new byte[total];

                        for (int by = 0; by < boxH; by++)
                        {
                            byte* row = scan0 + (minY + by) * stride + minX * 4;
                            int rowOffset = by * boxW;
                            for (int bx = 0; bx < boxW; bx++)
                            {
                                byte a = row[bx * 4 + 3];
                                if (a == 0) continue;

                                byte b = row[bx * 4 + 0];
                                byte g = row[bx * 4 + 1];
                                byte r = row[bx * 4 + 2];

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

                        // 1080i vertical 1-2-1 broadcast deflicker
                        if (boxH >= 3)
                        {
                            byte[] yOut = new byte[total];
                            byte[] uOut = new byte[total];
                            byte[] vOut = new byte[total];
                            byte[] aOut = new byte[total];

                            Action<int> filterRow = (by) =>
                            {
                                int topRow = (by > 0 ? by - 1 : 0) * boxW;
                                int midRow = by * boxW;
                                int botRow = (by < boxH - 1 ? by + 1 : boxH - 1) * boxW;
                                for (int bx = 0; bx < boxW; bx++)
                                {
                                    int tIdx = topRow + bx;
                                    int mIdx = midRow + bx;
                                    int bIdx = botRow + bx;
                                    int aT = aArr[tIdx];
                                    int aM = aArr[mIdx];
                                    int aB = aArr[bIdx];
                                    if ((aT | aM | aB) == 0) continue;

                                    if (aT == 255 && aM == 255 && aB == 255)
                                    {
                                        aOut[mIdx] = 255;
                                        yOut[mIdx] = (byte)((yArr[tIdx] + (yArr[mIdx] << 1) + yArr[bIdx] + 2) >> 2);
                                        uOut[mIdx] = (byte)((uArr[tIdx] + (uArr[mIdx] << 1) + uArr[bIdx] + 2) >> 2);
                                        vOut[mIdx] = (byte)((vArr[tIdx] + (vArr[mIdx] << 1) + vArr[bIdx] + 2) >> 2);
                                    }
                                    else
                                    {
                                        int wSum = aT + (aM << 1) + aB;
                                        aOut[mIdx] = (byte)((wSum + 2) >> 2);
                                        int half = wSum >> 1;
                                        yOut[mIdx] = (byte)((yArr[tIdx] * aT + ((yArr[mIdx] * aM) << 1) + yArr[bIdx] * aB + half) / wSum);
                                        uOut[mIdx] = (byte)((uArr[tIdx] * aT + ((uArr[mIdx] * aM) << 1) + uArr[bIdx] * aB + half) / wSum);
                                        vOut[mIdx] = (byte)((vArr[tIdx] * aT + ((vArr[mIdx] * aM) << 1) + vArr[bIdx] * aB + half) / wSum);
                                    }
                                }
                            };

                            if (boxH >= 64)
                                Parallel.For(0, boxH, filterRow);
                            else
                                for (int by = 0; by < boxH; by++) filterRow(by);

                            yArr = yOut;
                            uArr = uOut;
                            vArr = vOut;
                            aArr = aOut;
                        }

                        OnOverlayFrameUpdated?.Invoke(new PreparedHtmlOverlayFrame
                        {
                            StartX = minX,
                            StartY = minY,
                            Width = boxW,
                            Height = boxH,
                            Y = yArr,
                            U = uArr,
                            V = vArr,
                            A = aArr
                        });
                    }
                }

                OnPreviewBufferUpdated?.Invoke(prevCopy);
            }
            else
            {
                lock (_bufferLock)
                {
                    Buffer.BlockCopy(bgra1080, 0, _latestPgm1080Bgra, 0, BgraFrameBytes);
                    Buffer.BlockCopy(prevCopy, 0, _latestPreview480, 0, prevCopy.Length);
                    _hasCapturedFrame = true;
                }

                byte[] uyvy = MainWindow.ConvertBgra1080ToUyvy1080(_latestPgm1080Bgra);
                ApplyVerticalDeflickerUyvy1080(uyvy, _deflickerUyvy1080);
                lock (_bufferLock)
                {
                    Buffer.BlockCopy(_deflickerUyvy1080, 0, _latestUyvy1080, 0, _deflickerUyvy1080.Length);
                }

                lock (MainWindow._pgmLock)
                {
                    if (MainWindow.LatestHtmlSourceUyvy == null || MainWindow.LatestHtmlSourceUyvy.Length != UyvyFrameBytes)
                        MainWindow.LatestHtmlSourceUyvy = new byte[UyvyFrameBytes];
                    Buffer.BlockCopy(_deflickerUyvy1080, 0, MainWindow.LatestHtmlSourceUyvy, 0, _deflickerUyvy1080.Length);

                    if (MainWindow.ActiveSourceType == 7)
                    {
                        if (MainWindow._latestPgmFrame == null || MainWindow._latestPgmFrame.Length != BgraFrameBytes)
                            MainWindow._latestPgmFrame = new byte[BgraFrameBytes];
                        Buffer.BlockCopy(_latestPgm1080Bgra, 0, MainWindow._latestPgmFrame, 0, BgraFrameBytes);
                    }
                }

                OnPreviewBufferUpdated?.Invoke(prevCopy);
            }
        }

        private static unsafe void ApplyVerticalDeflickerUyvy1080(byte[] src, byte[] dst)
        {
            const int rowBytes = FrameWidth * 2;
            const int height = FrameHeight;
            Buffer.BlockCopy(src, 0, dst, 0, rowBytes);
            Buffer.BlockCopy(src, (height - 1) * rowBytes, dst, (height - 1) * rowBytes, rowBytes);

            fixed (byte* pSrc = src)
            fixed (byte* pDst = dst)
            {
                byte* srcPtr = pSrc;
                byte* dstPtr = pDst;
                Parallel.For(1, height - 1, y =>
                {
                    byte* sTop = srcPtr + (y - 1) * rowBytes;
                    byte* sMid = srcPtr + y * rowBytes;
                    byte* sBot = srcPtr + (y + 1) * rowBytes;
                    byte* dRow = dstPtr + y * rowBytes;
                    for (int i = 0; i < rowBytes; i++)
                    {
                        dRow[i] = (byte)((sTop[i] + (sMid[i] << 1) + sBot[i] + 2) >> 2);
                    }
                });
            }
        }

        public byte[]? GetLatestPreviewPixels()
        {
            lock (_bufferLock)
            {
                if (!_hasCapturedFrame) return null;
                return (byte[])_latestPreview480.Clone();
            }
        }

        public byte[]? GetLatestPgmPixels()
        {
            lock (_bufferLock)
            {
                if (!_hasCapturedFrame) return null;
                return (byte[])_latestPgm1080Bgra.Clone();
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            try { _browser?.Dispose(); } catch { }
            _browser = null;
        }
    }
}
