using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
        private volatile int _targetFps = 60;

        public int TargetFps
        {
            get => _targetFps;
            set
            {
                int clamped = Math.Clamp(value, 5, 60);
                _targetFps = clamped;
                try
                {
                    if (_browser != null && !_browser.IsDisposed && _browser.IsBrowserInitialized)
                    {
                        var host = _browser.GetBrowserHost();
                        if (host != null) host.WindowlessFrameRate = clamped;
                    }
                }
                catch { }
            }
        }

        private static readonly object _cefInitLock = new object();
        private static bool _cefInitialized = false;

        private readonly bool _isTransparentOverlay;
        private readonly string _instanceName;
        private ChromiumWebBrowser? _browser;
        private volatile bool _isRunning = false;
        private volatile bool _isDisposed = false;
        private bool _isMuted = false;
        private readonly D3D11SharedTextureReader _d3d11Reader = new D3D11SharedTextureReader();
        private bool _loggedAcceleratedPaint = false;

        private byte[] _paintCopy1080 = new byte[BgraFrameBytes];
        private byte[] _work1080 = new byte[BgraFrameBytes];
        private readonly object _paintSwapLock = new object();
        private volatile bool _hasPendingPaint = false;
        private int _isProcessingFrame = 0;

        // Lightweight OSR telemetry (logged every 5s while painting)
        private long _statWindowStartMs = 0;
        private long _statLastPaintMs = 0;
        private int _statPaintCount = 0;
        private int _statDropCount = 0;
        private long _statMaxPaintGapMs = 0;
        private long _statProcTicksTotal = 0;
        private int _statProcCount = 0;

        private readonly byte[] _latestPreview480 = new byte[480 * 270 * 4];
        private readonly byte[][] _previewBuffers = { new byte[480 * 270 * 4], new byte[480 * 270 * 4] };
        private int _previewPingPong = 0;
        private readonly byte[] _latestPgm1080Bgra = new byte[BgraFrameBytes];
        private readonly byte[] _latestUyvy1080 = new byte[UyvyFrameBytes];
        private readonly byte[] _uyvyScratch1080 = new byte[UyvyFrameBytes];
        private readonly byte[] _deflickerUyvy1080 = new byte[UyvyFrameBytes];
        private readonly int[] _rowMinX = new int[FrameHeight];
        private readonly int[] _rowMaxX = new int[FrameHeight];
        private byte[]? _ovScratchY, _ovScratchU, _ovScratchV, _ovScratchA;
        private readonly byte[][] _ovPoolY = new byte[2][];
        private readonly byte[][] _ovPoolU = new byte[2][];
        private readonly byte[][] _ovPoolV = new byte[2][];
        private readonly byte[][] _ovPoolA = new byte[2][];
        private int _ovPingPong = 0;
        private readonly object _bufferLock = new object();
        private bool _hasCapturedFrame = false;
        private long _lastPreviewDispatchMs = 0;

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
                        settings.CefCommandLineArgs.Add("disable-gpu-vsync", "1");

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
                WindowlessFrameRate = _targetFps,
                BackgroundColor = _isTransparentOverlay ? 0x00000000u : 0xFF000000u
            };

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var browser = new ChromiumWebBrowser(initialUrl, browserSettings, requestContext: null, automaticallyCreateBrowser: false)
            {
                Size = new System.Drawing.Size(FrameWidth, FrameHeight)
            };

            browser.BrowserInitialized += (s, e) =>
            {
                try
                {
                    var host = browser.GetBrowserHost();
                    host?.SetAudioMuted(_isMuted);
                    if (host != null) host.WindowlessFrameRate = _targetFps;
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
            browser.RenderHandler = new AcceleratedRenderHandler(browser, this);
            _browser = browser;

            var windowInfo = new WindowInfo();
            windowInfo.SetAsWindowless(IntPtr.Zero);
            windowInfo.SharedTextureEnabled = true;
            browser.CreateBrowser(windowInfo, browserSettings);

            if (browser.IsBrowserInitialized)
            {
                try
                {
                    var host = browser.GetBrowserHost();
                    host?.SetAudioMuted(_isMuted);
                    if (host != null) host.WindowlessFrameRate = _targetFps;
                }
                catch { }
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
                var host = _browser.GetBrowserHost();
                host?.WasHidden(false);
                host?.SetAudioMuted(_isMuted);
                if (host != null) host.WindowlessFrameRate = _targetFps;
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

        private void RecordPaintStatsLocked()
        {
            long nowMs = Environment.TickCount64;
            if (_statWindowStartMs == 0) _statWindowStartMs = nowMs;
            if (_statLastPaintMs != 0)
            {
                long gap = nowMs - _statLastPaintMs;
                if (gap > _statMaxPaintGapMs) _statMaxPaintGapMs = gap;
            }
            _statLastPaintMs = nowMs;
            _statPaintCount++;
            if (_hasPendingPaint) _statDropCount++;
        }

        internal void HandleAcceleratedPaint(AcceleratedPaintInfo info)
        {
            if (!_isRunning || _isDisposed || info.SharedTextureHandle == IntPtr.Zero) return;

            bool copied;
            lock (_paintSwapLock)
            {
                copied = _d3d11Reader.TryCopySharedTexture(info.SharedTextureHandle, FrameWidth, FrameHeight, _paintCopy1080);
                if (copied)
                {
                    RecordPaintStatsLocked();
                    _hasPendingPaint = true;
                }
            }

            if (copied)
            {
                if (!_loggedAcceleratedPaint)
                {
                    _loggedAcceleratedPaint = true;
                    MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Using D3D11 OnAcceleratedPaint (SharedTextureEnabled)");
                }
                if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) == 0)
                {
                    Task.Run(ProcessPendingFramesLoop);
                }
            }
        }

        private sealed class AcceleratedRenderHandler : DefaultRenderHandler
        {
            private readonly HtmlBrowserSource _owner;

            public AcceleratedRenderHandler(ChromiumWebBrowser browser, HtmlBrowserSource owner) : base(browser)
            {
                _owner = owner;
            }

            public override void OnAcceleratedPaint(PaintElementType type, CefSharp.Structs.Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
            {
                if (type == PaintElementType.View)
                {
                    _owner.HandleAcceleratedPaint(acceleratedPaintInfo);
                }
            }
        }

        private unsafe void OnBrowserPaint(object? sender, OnPaintEventArgs e)
        {
            if (!_isRunning || e.IsPopup || e.BufferHandle == IntPtr.Zero) return;
            if (e.Width != FrameWidth || e.Height != FrameHeight) return;

            lock (_paintSwapLock)
            {
                RecordPaintStatsLocked();

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
                            // Zero-copy reference swap instead of 8.3 MB BlockCopy
                            (_paintCopy1080, _work1080) = (_work1080, _paintCopy1080);
                            _hasPendingPaint = false;
                        }
                    }

                    if (!hasFrame) break;
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    ProcessBgra1080Frame(_work1080);
                    long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    _statProcTicksTotal += elapsed;
                    _statProcCount++;

                    long nowMs = Environment.TickCount64;
                    long winStart = _statWindowStartMs;
                    if (winStart != 0 && (nowMs - winStart) >= 5000)
                    {
                        int paints, drops;
                        long maxGap;
                        lock (_paintSwapLock)
                        {
                            paints = _statPaintCount;
                            drops = _statDropCount;
                            maxGap = _statMaxPaintGapMs;
                            _statPaintCount = 0;
                            _statDropCount = 0;
                            _statMaxPaintGapMs = 0;
                            _statWindowStartMs = nowMs;
                        }
                        double secs = (nowMs - winStart) / 1000.0;
                        double fps = paints / secs;
                        double avgProcMs = _statProcCount > 0
                            ? (_statProcTicksTotal * 1000.0 / System.Diagnostics.Stopwatch.Frequency) / _statProcCount
                            : 0.0;
                        _statProcTicksTotal = 0;
                        _statProcCount = 0;
                        MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] OSR 5s: targetFps={_targetFps}, paintFps={fps:F1}, dropped={drops}, maxGap={maxGap}ms, avgProc={avgProcMs:F2}ms");
                    }
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
            long nowMs = Environment.TickCount64;
            bool emitPreview = (nowMs - _lastPreviewDispatchMs) >= 15;
            byte[]? prevCopy = null;
            if (emitPreview)
            {
                _lastPreviewDispatchMs = nowMs;
                _previewPingPong ^= 1;
                prevCopy = _previewBuffers[_previewPingPong];
                Downsample480x270(bgra1080, prevCopy);
            }

            if (_isTransparentOverlay)
            {
                fixed (byte* scan0 = bgra1080)
                {
                    const int stride = FrameWidth * 4;
                    byte* basePtr = scan0;
                    int[] rowMinX = _rowMinX;
                    int[] rowMaxX = _rowMaxX;
                    Parallel.For(0, FrameHeight, y =>
                    {
                        uint* row32 = (uint*)(basePtr + y * stride);
                        int rMin = FrameWidth;
                        int rMax = -1;
                        for (int x = 0; x < FrameWidth; x++)
                        {
                            if ((row32[x] & 0xFF000000u) != 0)
                            {
                                rMin = x;
                                break;
                            }
                        }
                        if (rMin < FrameWidth)
                        {
                            for (int x = FrameWidth - 1; x >= rMin; x--)
                            {
                                if ((row32[x] & 0xFF000000u) != 0)
                                {
                                    rMax = x;
                                    break;
                                }
                            }
                        }
                        rowMinX[y] = rMin;
                        rowMaxX[y] = rMax;
                    });

                    int minX = FrameWidth, maxX = -1, minY = FrameHeight, maxY = -1;
                    for (int y = 0; y < FrameHeight; y++)
                    {
                        if (rowMaxX[y] >= 0)
                        {
                            if (rowMinX[y] < minX) minX = rowMinX[y];
                            if (rowMaxX[y] > maxX) maxX = rowMaxX[y];
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
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

                        int slot = _ovPingPong ^ 1;
                        _ovPingPong = slot;
                        if (_ovPoolY[slot] == null || _ovPoolY[slot].Length < total)
                        {
                            const int maxTotal = FrameWidth * FrameHeight;
                            _ovPoolY[slot] = new byte[maxTotal];
                            _ovPoolU[slot] = new byte[maxTotal];
                            _ovPoolV[slot] = new byte[maxTotal];
                            _ovPoolA[slot] = new byte[maxTotal];
                        }

                        byte[] yOut = _ovPoolY[slot];
                        byte[] uOut = _ovPoolU[slot];
                        byte[] vOut = _ovPoolV[slot];
                        byte[] aOut = _ovPoolA[slot];

                        byte[] yArr;
                        byte[] uArr;
                        byte[] vArr;
                        byte[] aArr;

                        if (boxH >= 3)
                        {
                            if (_ovScratchY == null || _ovScratchY.Length < total)
                            {
                                const int maxTotal = FrameWidth * FrameHeight;
                                _ovScratchY = new byte[maxTotal];
                                _ovScratchU = new byte[maxTotal];
                                _ovScratchV = new byte[maxTotal];
                                _ovScratchA = new byte[maxTotal];
                            }
                            yArr = _ovScratchY;
                            uArr = _ovScratchU!;
                            vArr = _ovScratchV!;
                            aArr = _ovScratchA!;
                        }
                        else
                        {
                            yArr = yOut;
                            uArr = uOut;
                            vArr = vOut;
                            aArr = aOut;
                        }

                        Action<int> convertRow = (by) =>
                        {
                            byte* row = basePtr + (minY + by) * stride + minX * 4;
                            int rowOffset = by * boxW;
                            for (int bx = 0; bx < boxW; bx++)
                            {
                                int idx = rowOffset + bx;
                                byte a = row[bx * 4 + 3];
                                if (a == 0)
                                {
                                    aArr[idx] = 0;
                                    continue;
                                }

                                byte b = row[bx * 4 + 0];
                                byte g = row[bx * 4 + 1];
                                byte r = row[bx * 4 + 2];

                                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                                int uVal = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                                int vVal = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

                                if (yVal < 16) yVal = 16; else if (yVal > 235) yVal = 235;
                                if (uVal < 16) uVal = 16; else if (uVal > 240) uVal = 240;
                                if (vVal < 16) vVal = 16; else if (vVal > 240) vVal = 240;

                                yArr[idx] = (byte)yVal;
                                uArr[idx] = (byte)uVal;
                                vArr[idx] = (byte)vVal;
                                aArr[idx] = a;
                            }
                        };

                        if (boxH >= 64)
                            Parallel.For(0, boxH, convertRow);
                        else
                            for (int by = 0; by < boxH; by++) convertRow(by);

                        // 1080i vertical 1-2-1 broadcast deflicker
                        if (boxH >= 3)
                        {
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
                                    if ((aT | aM | aB) == 0)
                                    {
                                        aOut[mIdx] = 0;
                                        continue;
                                    }

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
                        }

                        OnOverlayFrameUpdated?.Invoke(new PreparedHtmlOverlayFrame
                        {
                            StartX = minX,
                            StartY = minY,
                            Width = boxW,
                            Height = boxH,
                            Y = yOut,
                            U = uOut,
                            V = vOut,
                            A = aOut
                        });
                    }
                }

                if (prevCopy != null)
                {
                    OnPreviewBufferUpdated?.Invoke(prevCopy);
                }
            }
            else
            {
                bool isPgmActive = MainWindow.ActiveSourceType == 7;
                if (isPgmActive)
                {
                    MainWindow.ConvertBgra1080ToUyvy1080(bgra1080, _uyvyScratch1080);
                    ApplyVerticalDeflickerUyvy1080(_uyvyScratch1080, _deflickerUyvy1080);

                    lock (_bufferLock)
                    {
                        if (prevCopy != null)
                        {
                            Buffer.BlockCopy(bgra1080, 0, _latestPgm1080Bgra, 0, BgraFrameBytes);
                            Buffer.BlockCopy(prevCopy, 0, _latestPreview480, 0, prevCopy.Length);
                        }
                        Buffer.BlockCopy(_deflickerUyvy1080, 0, _latestUyvy1080, 0, _deflickerUyvy1080.Length);
                        _hasCapturedFrame = true;
                    }

                    lock (MainWindow._pgmLock)
                    {
                        if (MainWindow.LatestHtmlSourceUyvy == null || MainWindow.LatestHtmlSourceUyvy.Length != UyvyFrameBytes)
                            MainWindow.LatestHtmlSourceUyvy = new byte[UyvyFrameBytes];
                        Buffer.BlockCopy(_deflickerUyvy1080, 0, MainWindow.LatestHtmlSourceUyvy, 0, _deflickerUyvy1080.Length);
                    }
                }
                else
                {
                    lock (_bufferLock)
                    {
                        if (prevCopy != null)
                        {
                            Buffer.BlockCopy(bgra1080, 0, _latestPgm1080Bgra, 0, BgraFrameBytes);
                            Buffer.BlockCopy(prevCopy, 0, _latestPreview480, 0, prevCopy.Length);
                        }
                        _hasCapturedFrame = true;
                    }
                }

                if (prevCopy != null)
                {
                    OnPreviewBufferUpdated?.Invoke(prevCopy);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void ApplyVerticalDeflickerUyvy1080(byte[] src, byte[] dst)
        {
            const int rowBytes = FrameWidth * 2; // 3840 (multiple of 32 and 16)
            const int height = FrameHeight;
            Buffer.BlockCopy(src, 0, dst, 0, rowBytes);
            Buffer.BlockCopy(src, (height - 1) * rowBytes, dst, (height - 1) * rowBytes, rowBytes);

            fixed (byte* pSrc = src)
            fixed (byte* pDst = dst)
            {
                byte* srcPtr = pSrc;
                byte* dstPtr = pDst;
                bool useAvx2 = Avx2.IsSupported;
                bool useSse2 = !useAvx2 && Sse2.IsSupported;

                Parallel.For(1, height - 1, y =>
                {
                    byte* sTop = srcPtr + (y - 1) * rowBytes;
                    byte* sMid = srcPtr + y * rowBytes;
                    byte* sBot = srcPtr + (y + 1) * rowBytes;
                    byte* dRow = dstPtr + y * rowBytes;
                    int i = 0;
                    if (useAvx2)
                    {
                        for (; i <= rowBytes - 32; i += 32)
                        {
                            Vector256<byte> vTop = Avx.LoadVector256(sTop + i);
                            Vector256<byte> vMid = Avx.LoadVector256(sMid + i);
                            Vector256<byte> vBot = Avx.LoadVector256(sBot + i);
                            Vector256<byte> vAvg = Avx2.Average(Avx2.Average(vTop, vBot), vMid);
                            Avx.Store(dRow + i, vAvg);
                        }
                    }
                    else if (useSse2)
                    {
                        for (; i <= rowBytes - 16; i += 16)
                        {
                            Vector128<byte> vTop = Sse2.LoadVector128(sTop + i);
                            Vector128<byte> vMid = Sse2.LoadVector128(sMid + i);
                            Vector128<byte> vBot = Sse2.LoadVector128(sBot + i);
                            Vector128<byte> vAvg = Sse2.Average(Sse2.Average(vTop, vBot), vMid);
                            Sse2.Store(dRow + i, vAvg);
                        }
                    }
                    for (; i < rowBytes; i++)
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
            _d3d11Reader.Dispose();
        }
    }
}
