using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

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
        private readonly bool _isTransparentOverlay;
        private readonly string _instanceName;
        private Window? _hostWindow;
        private WebView2? _webView;
        private bool _isInitialized = false;
        private bool _isRunning = false;
        private int _isCapturingFrame = 0;
        private CancellationTokenSource? _captureCts;

        private readonly byte[] _latestPreview480 = new byte[480 * 270 * 4];
        private readonly byte[] _latestPgm1080Bgra = new byte[1920 * 1080 * 4];
        private readonly byte[] _latestUyvy1080 = new byte[1920 * 1080 * 2];
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

        private async Task EnsureInitializedAsync()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null) return;

            var tcs = new TaskCompletionSource<bool>();

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (_hostWindow == null)
                    {
                        _webView = new WebView2
                        {
                            Width = 1920,
                            Height = 1080,
                            DefaultBackgroundColor = _isTransparentOverlay
                                ? System.Drawing.Color.Transparent
                                : System.Drawing.Color.Black
                        };

                        _hostWindow = new Window
                        {
                            Title = "DeckLinkSwitcher_HtmlHost_" + _instanceName,
                            Width = 1920,
                            Height = 1080,
                            WindowStyle = WindowStyle.None,
                            ResizeMode = ResizeMode.NoResize,
                            ShowInTaskbar = false,
                            ShowActivated = false,
                            AllowsTransparency = false,
                            Left = -32000,
                            Top = -32000,
                            Content = _webView
                        };
                        _hostWindow.Show();
                    }

                    string userDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DecklinkSwitcher",
                        "WebView2_" + _instanceName);

                    var options = new CoreWebView2EnvironmentOptions(
                        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required");

                    var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                    await _webView!.EnsureCoreWebView2Async(env);

                    _webView.DefaultBackgroundColor = _isTransparentOverlay
                        ? System.Drawing.Color.Transparent
                        : System.Drawing.Color.Black;

                    if (_isTransparentOverlay)
                    {
                        _webView.CoreWebView2.IsMuted = true;
                    }

                    _webView.CoreWebView2.NavigationStarting += (s, e) =>
                    {
                        OnStatusChanged?.Invoke("LOADING...", false, false);
                    };

                    _webView.CoreWebView2.NavigationCompleted += async (s, e) =>
                    {
                        if (e.IsSuccess)
                        {
                            if (_isTransparentOverlay && _webView?.CoreWebView2 != null)
                            {
                                try
                                {
                                    await _webView.CoreWebView2.ExecuteScriptAsync(
                                        "document.documentElement.style.background='transparent'; if(document.body) document.body.style.background='transparent';");
                                }
                                catch { }
                            }
                            OnStatusChanged?.Invoke("LIVE", true, false);
                        }
                        else
                        {
                            OnStatusChanged?.Invoke($"ERR: {e.WebErrorStatus}", false, true);
                        }
                    };

                    _isInitialized = true;
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Init error: {ex.Message}");
                    OnStatusChanged?.Invoke("WEBVIEW2 ERR", false, true);
                    tcs.TrySetResult(false);
                }
            });

            await tcs.Task;
        }

        public async Task StartAsync(string rawUrl)
        {
            string url = NormalizeUrl(rawUrl);
            if (string.IsNullOrEmpty(url))
            {
                OnStatusChanged?.Invoke("NO URL", false, true);
                return;
            }

            CurrentUrl = url;
            OnStatusChanged?.Invoke("LOADING...", false, false);

            await EnsureInitializedAsync();
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;

            _isRunning = true;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _webView.CoreWebView2.Navigate(url);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Navigate error: {ex.Message}");
                    OnStatusChanged?.Invoke("INVALID URL", false, true);
                }
            });

            StartCaptureLoop();
        }

        public void Reload()
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try { _webView.CoreWebView2.Reload(); } catch { }
            });
        }

        public void SetMuted(bool muted)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_webView?.CoreWebView2 != null)
                        _webView.CoreWebView2.IsMuted = muted;
                }
                catch { }
            });
        }

        public void Stop()
        {
            _isRunning = false;
            _captureCts?.Cancel();
            _captureCts = null;
            _hasCapturedFrame = false;

            if (_isInitialized && _webView?.CoreWebView2 != null)
            {
                Application.Current.Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        if (_screencastActive)
                        {
                            _screencastActive = false;
                            await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}");
                        }
                    }
                    catch { }
                    try { _webView.CoreWebView2.Navigate("about:blank"); } catch { }
                });
            }

            OnOverlayFrameUpdated?.Invoke(null);
            OnStatusChanged?.Invoke("OFFLINE", false, false);
        }

        private bool _screencastHooked = false;
        private bool _screencastActive = false;
        private int _isProcessingScreencast = 0;

        // Event-driven frames pushed by Chromium on every paint (no per-frame screenshot polling).
        private async Task StartScreencastAsync()
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var core = _webView?.CoreWebView2;
                    if (core == null) return;

                    if (!_screencastHooked)
                    {
                        var receiver = core.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
                        receiver.DevToolsProtocolEventReceived += async (s, e) =>
                        {
                            try
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
                                int sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();
                                string b64 = doc.RootElement.GetProperty("data").GetString() ?? "";

                                // Ack right away so Chromium keeps sending frames
                                _ = core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", "{\"sessionId\":" + sessionId + "}");

                                if (!_isRunning || b64.Length == 0) return;
                                if (Interlocked.CompareExchange(ref _isProcessingScreencast, 1, 0) != 0) return; // drop if busy

                                byte[] bytes = Convert.FromBase64String(b64);
                                _ = Task.Run(() =>
                                {
                                    try { if (_isRunning) ProcessCapturedBytes(bytes); }
                                    catch (Exception ex) { MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Screencast process error: {ex.Message}"); }
                                    finally { Interlocked.Exchange(ref _isProcessingScreencast, 0); }
                                });
                            }
                            catch (Exception ex)
                            {
                                MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Screencast frame error: {ex.Message}");
                            }
                        };
                        _screencastHooked = true;
                    }

                    await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                    if (_isTransparentOverlay)
                    {
                        // Keep alpha: transparent page background + PNG frames
                        await core.CallDevToolsProtocolMethodAsync("Emulation.setDefaultBackgroundColorOverride",
                            "{\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":0}}");
                    }
                    string fmt = _isTransparentOverlay ? "\"format\":\"png\"" : "\"format\":\"jpeg\",\"quality\":88";
                    await core.CallDevToolsProtocolMethodAsync("Page.startScreencast",
                        "{" + fmt + ",\"maxWidth\":1920,\"maxHeight\":1080,\"everyNthFrame\":1}");
                    _screencastActive = true;
                }).Task.Unwrap();
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Screencast start failed, falling back to polling: {ex.Message}");
                _screencastActive = false;
                StartPollingLoop();
            }
        }

        private void StartCaptureLoop()
        {
            _ = StartScreencastAsync();
        }

        private void StartPollingLoop()
        {
            _captureCts?.Cancel();
            _captureCts = new CancellationTokenSource();
            var token = _captureCts.Token;

            int intervalMs = _isTransparentOverlay ? 50 : 40; // 20-25 fps

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && _isRunning)
                {
                    try
                    {
                        if (Interlocked.CompareExchange(ref _isCapturingFrame, 1, 0) == 0)
                        {
                            try
                            {
                                byte[]? pngOrJpgBytes = null;
                                await Application.Current.Dispatcher.InvokeAsync(async () =>
                                {
                                    if (!_isRunning || _webView?.CoreWebView2 == null) return;
                                    try
                                    {
                                        using var ms = new MemoryStream();
                                        var format = CoreWebView2CapturePreviewImageFormat.Png;
                                        await _webView.CoreWebView2.CapturePreviewAsync(format, ms);
                                        if (ms.Length > 0)
                                        {
                                            pngOrJpgBytes = ms.ToArray();
                                        }
                                    }
                                    catch (Exception capEx) { MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Capture error: {capEx.Message}"); }
                                }).Task.Unwrap();

                                if (pngOrJpgBytes != null && pngOrJpgBytes.Length > 0 && _isRunning)
                                {
                                    try { ProcessCapturedBytes(pngOrJpgBytes); }
                                    catch (Exception procEx) { MainWindow.Log($"[HtmlBrowserSource:{_instanceName}] Process error: {procEx}"); }
                                }
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _isCapturingFrame, 0);
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }

                    try
                    {
                        await Task.Delay(intervalMs, token);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        private unsafe void ProcessCapturedBytes(byte[] encodedBytes)
        {
            using var ms = new MemoryStream(encodedBytes);
            using var decodedBmp = new System.Drawing.Bitmap(ms);

            using var bmp1080 = (_isTransparentOverlay && decodedBmp.Width == 1920 && decodedBmp.Height == 1080 && decodedBmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb)
                ? (System.Drawing.Bitmap)decodedBmp.Clone()
                : ResizeTo1080Argb(decodedBmp);

            // Downsample 480x270 for UI preview
            using var bmp480 = new System.Drawing.Bitmap(480, 270, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g480 = System.Drawing.Graphics.FromImage(bmp480))
            {
                g480.Clear(System.Drawing.Color.Transparent);
                g480.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g480.DrawImage(bmp1080, 0, 0, 480, 270);
            }

            byte[] prevCopy = new byte[480 * 270 * 4];
            var data480 = bmp480.LockBits(new System.Drawing.Rectangle(0, 0, 480, 270), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(data480.Scan0, prevCopy, 0, prevCopy.Length);
            }
            finally
            {
                bmp480.UnlockBits(data480);
            }

            if (_isTransparentOverlay)
            {
                var data1080 = bmp1080.LockBits(new System.Drawing.Rectangle(0, 0, 1920, 1080), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    byte* scan0 = (byte*)data1080.Scan0;
                    int stride = data1080.Stride;

                    int minX = 1920, maxX = -1, minY = 1080, maxY = -1;
                    for (int y = 0; y < 1080; y++)
                    {
                        byte* row = scan0 + y * stride;
                        for (int x = 0; x < 1920; x++)
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
                        if (maxX >= 1920) maxX = 1919;

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
                finally
                {
                    bmp1080.UnlockBits(data1080);
                }

                OnPreviewBufferUpdated?.Invoke(prevCopy);
            }
            else
            {
                // Opaque 1080p switcher source
                var data1080 = bmp1080.LockBits(new System.Drawing.Rectangle(0, 0, 1920, 1080), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    lock (_bufferLock)
                    {
                        Marshal.Copy(data1080.Scan0, _latestPgm1080Bgra, 0, _latestPgm1080Bgra.Length);
                        Buffer.BlockCopy(prevCopy, 0, _latestPreview480, 0, prevCopy.Length);
                        _hasCapturedFrame = true;
                    }
                }
                finally
                {
                    bmp1080.UnlockBits(data1080);
                }

                byte[] uyvy = MainWindow.ConvertBgra1080ToUyvy1080(_latestPgm1080Bgra);
                lock (_bufferLock)
                {
                    Buffer.BlockCopy(uyvy, 0, _latestUyvy1080, 0, uyvy.Length);
                }

                lock (MainWindow._pgmLock)
                {
                    if (MainWindow.LatestHtmlSourceUyvy == null || MainWindow.LatestHtmlSourceUyvy.Length != 1920 * 1080 * 2)
                        MainWindow.LatestHtmlSourceUyvy = new byte[1920 * 1080 * 2];
                    Buffer.BlockCopy(uyvy, 0, MainWindow.LatestHtmlSourceUyvy, 0, uyvy.Length);

                    if (MainWindow.ActiveSourceType == 7)
                    {
                        if (MainWindow._latestPgmFrame == null || MainWindow._latestPgmFrame.Length != 1920 * 1080 * 4)
                            MainWindow._latestPgmFrame = new byte[1920 * 1080 * 4];
                        Buffer.BlockCopy(_latestPgm1080Bgra, 0, MainWindow._latestPgmFrame, 0, _latestPgm1080Bgra.Length);
                    }
                }

                OnPreviewBufferUpdated?.Invoke(prevCopy);
            }
        }

        private System.Drawing.Bitmap ResizeTo1080Argb(System.Drawing.Bitmap src) => ResizeTo1080ArgbCore(src, _isTransparentOverlay);

        private static System.Drawing.Bitmap ResizeTo1080ArgbCore(System.Drawing.Bitmap src, bool transparent)
        {
            var dst = new System.Drawing.Bitmap(1920, 1080, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(dst);
            g.Clear(transparent ? System.Drawing.Color.Transparent : System.Drawing.Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(src, 0, 0, 1920, 1080);
            return dst;
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
            Stop();
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                try { _webView?.Dispose(); } catch { }
                try { _hostWindow?.Close(); } catch { }
            });
        }
    }
}

