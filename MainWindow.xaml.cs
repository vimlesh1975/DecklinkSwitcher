using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DeckLinkAPI;

namespace DecklinkSwitcher
{
    public partial class MainWindow : Window
    {
        public static System.Collections.ObjectModel.ObservableCollection<DeckLinkInputViewModel> DynamicInputs { get; } = new();
        public static System.Collections.ObjectModel.ObservableCollection<ScreenInputViewModel> DynamicScreens { get; } = new();
        public static System.Collections.ObjectModel.ObservableCollection<WebcamInputViewModel> DynamicWebcams { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<InputSelectionViewModel> InputSelectors { get; } = new();

        public static void SetActiveSource(int type, string name, DeckLinkDevice device)
        {
            var window = (MainWindow)Application.Current.MainWindow;
            window._activeSourceType = type;
            ActiveSourceType = type;
            window._activeInput = device;
            window._activeScreen = null;
            window._activeWebcam = null;
            ActiveInputName = name;
            window.UpdateProgramSourceFlags();
            
            if (device != null || type != 0)
                Log($"Switched to {name}");
            else
            {
                Log($"Switched to {name} (None)");
                window.ClearPgmPreview();
            }
        }


        private DeckLinkDevice _activeInput;
        private DeckLinkDevice _activeOutput;
        

        private System.Windows.Media.Imaging.WriteableBitmap _bmpOutput;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpColorBars;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpMatte;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpMedia;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpYouTube;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpNdi;
        private NdiSource _ndiSource = new NdiSource();
        public static float NdiAudioLevel = 1.0f;
        public static AudioState StateNdi = AudioState.AFV;
        internal static byte[]? LatestNdiUyvy = new byte[1920 * 1080 * 2];
        private System.Windows.Media.Imaging.WriteableBitmap _bmpHtmlSource;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpHtmlOverlay;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpNdiOverlay;
        private NdiOverlaySource _ndiOverlaySource = new NdiOverlaySource();
        public static volatile bool IsNdiOverlayEnabled = false;
        internal static volatile PreparedHtmlOverlayFrame? CurrentNdiOverlay = null;
        public static double NdiOverlayOpacityPercent = 100.0;
        private ScreenInputViewModel? _activeScreen;
        private WebcamInputViewModel? _activeWebcam;
        private SystemAudioSource _systemAudioSource = new();
        public static float SystemAudioLevel = 1.0f;
        public static AudioState StateSystemAudio = AudioState.AFV;
        
        private VlcMediaSource _mediaSource;
        private YouTubeSource _youtubeSource;
        private HtmlBrowserSource _htmlSource;
        private HtmlBrowserSource _htmlOverlaySource;
        public static float YouTubeAudioLevel = 1.0f;
        public static AudioState StateYouTube = AudioState.AFV;
        public static AudioState StateHtml = AudioState.AFV;
        private System.Collections.Generic.List<LocalAudioInput> _activeMics = new();
        private CancellationTokenSource? _syntheticCts;
        public static YouTubeStreamer YtStreamer = new YouTubeStreamer();
        public static LocalRecorder LclRecorder = new LocalRecorder();
        public static NdiSender NdiOut = new NdiSender();
        public static SrtStreamer SrtOut = new SrtStreamer();
        
        // Latest PGM frame (BGRA 1920x1080 or 480x270) — updated by all PGM sources, read by streaming loop
        internal static byte[]? _latestPgmFrame = null;
        internal static byte[]? LatestYouTubeUyvy = new byte[1920 * 1080 * 2];
        internal static byte[]? LatestHtmlSourceUyvy = new byte[1920 * 1080 * 2];
        internal static readonly object _pgmLock = new object();
        private static CancellationTokenSource? _streamLoopCts;

        internal sealed class PreparedLogoOverlay
        {
            public int Width { get; init; }
            public int Height { get; init; }
            public byte[] Y { get; init; } = Array.Empty<byte>();
            public byte[] U { get; init; } = Array.Empty<byte>();
            public byte[] V { get; init; } = Array.Empty<byte>();
            public byte[] A { get; init; } = Array.Empty<byte>();
        }

        private string _logoFilePath = "";
        private System.Drawing.Bitmap? _rawLogoBitmap = null;
        private System.Windows.Media.Imaging.BitmapImage? _logoPreviewImageSource = null;
        private bool _isUpdatingLogoUi = false;
        private bool _isLoadingSettings = true;

        public static volatile bool IsLogoOverlayEnabled = false;
        internal static volatile PreparedLogoOverlay? CurrentLogoOverlay = null;
        public static double LogoSizePercent = 15.0;
        public static double LogoOpacityPercent = 100.0;
        public static double LogoXPercent = 96.0;
        public static double LogoYPercent = 4.0;

        public static volatile bool IsHtmlOverlayEnabled = false;
        internal static volatile PreparedHtmlOverlayFrame? CurrentHtmlOverlay = null;
        public static double HtmlOverlayOpacityPercent = 100.0;


        private static string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "decklinkswitcher_log.txt");

        private System.Windows.Threading.DispatcherTimer _seekTimer;
        private bool _isDraggingSeek = false;
        private System.Diagnostics.PerformanceCounter _cpuCounter;

        private System.Windows.Point _dragStartPoint;
        private FrameworkElement? _potentialDragTile;
        private FrameworkElement? _draggedTile;
        private bool _isDragging = false;
        private System.Windows.Threading.DispatcherTimer _autoScrollTimer;
        private double _scrollDelta = 0;

        public MainWindow()
        {
            Log("=========================================");
            Log("Application Started");
            try
            {
                InitializeComponent();
                try
                {
                    string exeName = System.IO.Path.GetFileName(Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "DecklinkSwitcher.exe");
                    this.Title = exeName;
                }
                catch { }
                ((App)Application.Current).SetTheme(true);
                Task.Run(() => 
                {
                    try
                    {
                        var counter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                        counter.NextValue();
                        _cpuCounter = counter;
                    }
                    catch (Exception ex)
                    {
                        Log("PerformanceCounter init error: " + ex.ToString());
                    }
                });
            }
            catch (Exception ex)
            {
                Log("InitializeComponent error: " + ex.ToString());
            }
            
            _bmpColorBars = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpMatte = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpMedia = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpYouTube = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpHtmlSource = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpHtmlOverlay = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpNdiOverlay = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            PreviewColorBars.Source = _bmpColorBars;
            PreviewMatte.Source = _bmpMatte;
            PreviewMedia.Source = _bmpMedia;
            PreviewYouTube.Source = _bmpYouTube;
            _bmpNdi = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            PreviewNdi.Source = _bmpNdi;
            SetupNdiSource();
            Loaded += async (s, e) => { try { await RefreshNdiSourcesAsync(); await RefreshNdiOverlaySourcesAsync(); } catch { } };
            PreviewHtmlSource.Source = _bmpHtmlSource;
            PreviewHtmlOverlay.Source = _bmpHtmlOverlay;
            if (PreviewHtmlOverlayDedicated != null) PreviewHtmlOverlayDedicated.Source = _bmpHtmlOverlay;
            if (PreviewNdiOverlay != null) PreviewNdiOverlay.Source = _bmpNdiOverlay;
            if (PreviewNdiOverlayDedicated != null) PreviewNdiOverlayDedicated.Source = _bmpNdiOverlay;
            SetupNdiOverlaySource();
            
            try
            {
                _mediaSource = new VlcMediaSource();
                _mediaSource.OnVideoAndAudioArrived = (vBuf, w, h, rb, aBuf, aCount) => {
                    if (_activeSourceType == 3) 
                    {
                        // Push PGM frame to streamer on every VLC frame (not throttled like preview)
                        if (YtStreamer.IsStreaming)
                        {
                            try
                            {
                                byte[] bgraPreview = new byte[480 * 270 * 4];
                                unsafe
                                {
                                    fixed (byte* dest = bgraPreview)
                                    {
                                        DeckLinkDevice.ConvertUYVYToBGRA_Downsampled(vBuf, w, h, rb, (IntPtr)dest, 480, 270, 480 * 4);
                                    }
                                }
                                
                            }
                            catch { }
                        }

                        if (_activeOutput != null)
                        {
                            _activeOutput.ScheduleCustomFrame(vBuf, w, h, rb, aBuf, aCount);
                        }
                        else
                        {
                            uint mixedSampleCount = aCount > 0 ? aCount : 1920;
                            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(mixedSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName, "media-software-output");
                            if (modifiedAudioBuffer != IntPtr.Zero)
                            {
                                SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, mixedSampleCount);
                                if (YtStreamer.IsStreaming || LclRecorder.IsRecording || NdiOut.IsRunning || SrtOut.IsStreaming)
                                {
                                    byte[] audBytes = new byte[mixedSampleCount * 4];
                                    System.Runtime.InteropServices.Marshal.Copy(modifiedAudioBuffer, audBytes, 0, audBytes.Length);
                                    byte[]? uyvyBytes = null;
                                    if (vBuf != IntPtr.Zero && w == 1920 && h == 1080 && rb == 1920 * 2)
                                    {
                                        uyvyBytes = new byte[1920 * 1080 * 2];
                                        System.Runtime.InteropServices.Marshal.Copy(vBuf, uyvyBytes, 0, uyvyBytes.Length);
                                        ApplyLogoOverlayUyvy(uyvyBytes);
                                    }
                                    if (YtStreamer.IsStreaming) YtStreamer.PushFrame(audBytes, uyvyBytes);
                                    if (LclRecorder.IsRecording) LclRecorder.PushFrame(audBytes, uyvyBytes);
                                    if (NdiOut.IsRunning) NdiOut.PushFrame(audBytes, uyvyBytes);
                                    if (SrtOut.IsStreaming) SrtOut.PushFrame(audBytes, uyvyBytes);
                                }
                                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                            }
                        }
                    }
                };
                _mediaSource.OnPreviewBufferUpdated = (buf) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        _bmpMedia.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                        if (_activeSourceType == 3)
                        {
                            _bmpOutput?.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                            
                        }
                    });
                };
                _mediaSource.OnAudioLevelArrived = (l, r) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        double mediaL = l * _mediaSource.AudioLevel;
                        double mediaR = r * _mediaSource.AudioLevel;
                        AudioBarMediaL.Value = mediaL; AudioBarMediaR.Value = mediaR;
                        if (_activeSourceType == 3) 
                        { 
                            AudioOutputL.Value = mediaL * PgmAudioLevel; 
                            AudioOutputR.Value = mediaR * PgmAudioLevel; 
                        }
                    });
                };
            }
            catch (Exception ex)
            {
            }

            try
            {
                _youtubeSource = new YouTubeSource();
                _youtubeSource.OnPreviewBufferUpdated = (buf) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        _bmpYouTube.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                        if (_activeSourceType == 6)
                        {
                            _bmpOutput?.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                        }
                    });
                };
                _youtubeSource.OnAudioLevelArrived = (l, r) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        double ytL = l * _youtubeSource.AudioLevel;
                        double ytR = r * _youtubeSource.AudioLevel;
                        AudioBarYTL.Value = ytL; AudioBarYTR.Value = ytR;
                        if (_activeSourceType == 6)
                        {
                            AudioOutputL.Value = ytL * PgmAudioLevel;
                            AudioOutputR.Value = ytR * PgmAudioLevel;
                        }
                    });
                };
                _youtubeSource.OnStatusChanged = (status, isLive, isError) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        TxtYouTubeStatus.Text = status;
                        if (isError)
                        {
                            TxtYouTubeStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                            TxtYouTubeStatusIcon.Foreground = System.Windows.Media.Brushes.IndianRed;
                            PnlYouTubeStatusOverlay.Visibility = Visibility.Visible;
                            BtnConnectYouTube.IsEnabled = true;
                            BtnDisconnectYouTube.IsEnabled = false;
                        }
                        else if (isLive)
                        {
                            TxtYouTubeStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                            TxtYouTubeStatusIcon.Foreground = System.Windows.Media.Brushes.LimeGreen;
                            PnlYouTubeStatusOverlay.Visibility = Visibility.Collapsed;
                            BtnConnectYouTube.IsEnabled = false;
                            BtnDisconnectYouTube.IsEnabled = true;
                        }
                        else
                        {
                            TxtYouTubeStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
                            TxtYouTubeStatusIcon.Foreground = System.Windows.Media.Brushes.Red;
                            PnlYouTubeStatusOverlay.Visibility = Visibility.Visible;
                            BtnConnectYouTube.IsEnabled = true;
                            BtnDisconnectYouTube.IsEnabled = false;
                        }
                    });
                };
            }
            catch (Exception ex)
            {
            }

            try
            {
                _htmlSource = new HtmlBrowserSource(false, "Source");
                _htmlSource.OnPreviewBufferUpdated = (buf) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        _bmpHtmlSource.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                        if (_activeSourceType == 7)
                        {
                            _bmpOutput?.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                        }
                    });
                };
                _htmlSource.OnStatusChanged = (status, isLive, isError) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        TxtHtmlStatus.Text = status;
                        if (isError)
                        {
                            TxtHtmlStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                            PnlHtmlStatusOverlay.Visibility = Visibility.Visible;
                            BtnConnectHtmlSource.IsEnabled = true;
                            BtnDisconnectHtmlSource.IsEnabled = false;
                        }
                        else if (isLive)
                        {
                            TxtHtmlStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                            PnlHtmlStatusOverlay.Visibility = Visibility.Collapsed;
                            BtnConnectHtmlSource.IsEnabled = true;
                            BtnDisconnectHtmlSource.IsEnabled = true;
                            UpdateHtmlSourceMuteState();
                        }
                        else
                        {
                            TxtHtmlStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
                            PnlHtmlStatusOverlay.Visibility = Visibility.Visible;
                            BtnConnectHtmlSource.IsEnabled = true;
                            BtnDisconnectHtmlSource.IsEnabled = _htmlSource.IsRunning;
                        }
                    });
                };

                _htmlOverlaySource = new HtmlBrowserSource(true, "Overlay");
                _htmlOverlaySource.OnOverlayFrameUpdated = (frame) => {
                    CurrentHtmlOverlay = frame;
                };
                _htmlOverlaySource.OnPreviewBufferUpdated = (buf) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        _bmpHtmlOverlay.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                        if (TxtHtmlOverlayPreviewPlaceholder != null && TxtHtmlOverlayPreviewPlaceholder.Visibility != Visibility.Collapsed)
                            TxtHtmlOverlayPreviewPlaceholder.Visibility = Visibility.Collapsed;
                    });
                };
                _htmlOverlaySource.OnStatusChanged = (status, isLive, isError) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        if (TxtHtmlOverlayStatus != null)
                        {
                            TxtHtmlOverlayStatus.Text = "Status: " + status;
                            TxtHtmlOverlayStatus.Foreground = isError
                                ? System.Windows.Media.Brushes.IndianRed
                                : (isLive ? System.Windows.Media.Brushes.LimeGreen : (System.Windows.Media.Brush)FindResource("TextDim"));
                        }
                        if (!isLive && TxtHtmlOverlayPreviewPlaceholder != null)
                            TxtHtmlOverlayPreviewPlaceholder.Visibility = Visibility.Visible;
                    });
                };
            }
            catch (Exception ex)
            {
            }

            SystemAudioPlayer.Init();

            _seekTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _seekTimer.Tick += SeekTimer_Tick;
            _seekTimer.Start();

            SwitcherScrollViewer.PreviewMouseLeftButtonDown += SwitcherPanel_PreviewMouseLeftButtonDown;
            SwitcherScrollViewer.PreviewMouseMove += SwitcherPanel_PreviewMouseMove;
            SwitcherScrollViewer.PreviewMouseLeftButtonUp += SwitcherPanel_PreviewMouseLeftButtonUp;

            SwitcherPanel.DragOver += SwitcherPanel_DragOver;
            SwitcherPanel.Drop += SwitcherPanel_Drop;
            SwitcherPanel.GiveFeedback += SwitcherPanel_GiveFeedback;

            SwitcherScrollViewer.DragOver += SwitcherPanel_DragOver;
            SwitcherScrollViewer.Drop += SwitcherPanel_Drop;

            _autoScrollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _autoScrollTimer.Tick += AutoScrollTimer_Tick;

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        public void SaveCurrentSettings()
        {
            if (_isLoadingSettings) return;
            try
            {
                var settings = AppSettings.Load();
                
                // Safe dispatch in case called from non-UI thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (this.WindowState == System.Windows.WindowState.Maximized)
                    {
                        settings.WindowWidth = this.RestoreBounds.Width;
                        settings.WindowHeight = this.RestoreBounds.Height;
                        settings.WindowLeft = this.RestoreBounds.Left;
                        settings.WindowTop = this.RestoreBounds.Top;
                        settings.WindowMaximized = true;
                    }
                    else
                    {
                        settings.WindowWidth = this.Width;
                        settings.WindowHeight = this.Height;
                        settings.WindowLeft = this.Left;
                        settings.WindowTop = this.Top;
                        settings.WindowMaximized = false;
                    }
                    if (CmbOutput.SelectedItem is DeckLinkDeviceInfo dout) settings.OutputDevice = dout.DisplayName;
                    foreach (var selector in InputSelectors)
                    {
                        if (selector.SelectedDevice != null)
                            settings.InputDevices[selector.Label] = selector.SelectedDevice.DisplayName;
                        else
                            settings.InputDevices[selector.Label] = "";

                        settings.InputVideoFormats[selector.Label] = selector.SelectedFormat;
                    }
                    
                    settings.AudioLevelPgm = SldPgmAudio.Value;
                    settings.AudioLevelColorBars = SldColorBarsAudio.Value;
                    settings.AudioLevelMedia = SldMediaAudio.Value;
                    settings.MatteColorIndex = CmbMatteColor.SelectedIndex;
                    settings.SystemAudioMonitorEnabled = ChkSystemAudioOut.IsChecked == true;
                    settings.LoopMediaEnabled = ChkLoopMedia.IsChecked == true;
                    
                    settings.AudioStateColorBars = (AudioState)CmbAudioStateColorBars.SelectedIndex;
                    settings.AudioStateMedia = (AudioState)CmbAudioStateMedia.SelectedIndex;
                    if (CmbAudioStateYouTube != null && CmbAudioStateYouTube.SelectedIndex >= 0)
                        settings.AudioStateYouTube = (AudioState)CmbAudioStateYouTube.SelectedIndex;
                    if (SldYouTubeAudio != null) settings.AudioLevelYouTube = SldYouTubeAudio.Value;
                    if (SldNdiAudio != null) settings.AudioLevelNdi = SldNdiAudio.Value;
                    if (CmbAudioStateNdi != null && CmbAudioStateNdi.SelectedIndex >= 0)
                        settings.AudioStateNdi = (AudioState)CmbAudioStateNdi.SelectedIndex;
                    settings.NdiSourceName = (CmbNdiSources?.SelectedItem as NdiSourceInfo)?.Name ?? _pendingNdiSourceName ?? settings.NdiSourceName;
                    if (SldYouTubeAudioDelay != null) settings.YouTubeAudioDelayMs = (int)SldYouTubeAudioDelay.Value;
                    if (ChkInvertDeckLinkR != null) settings.InvertDeckLinkRightChannel = ChkInvertDeckLinkR.IsChecked == true;
                    if (TxtYouTubeUrl != null) settings.YouTubeSourceUrl = TxtYouTubeUrl.Text.Trim();
                    if (SldSystemAudio != null) settings.AudioLevelSystemAudio = SldSystemAudio.Value;
                    if (CmbAudioStateSystemAudio != null && CmbAudioStateSystemAudio.SelectedIndex >= 0)
                        settings.AudioStateSystemAudio = (AudioState)CmbAudioStateSystemAudio.SelectedIndex;
                    if (CmbSystemAudioDevice != null && CmbSystemAudioDevice.SelectedItem is RenderDeviceInfo rdi)
                        settings.SelectedSystemAudioDevice = rdi.DeviceId;
                    
                    foreach (var input in DynamicInputs)
                    {
                        settings.InputAudioLevels[input.DisplayName] = input.AudioLevel;
                        settings.InputAudioStates[input.DisplayName] = input.State;
                    }

                    for (int i = 0; i < DynamicWebcams.Count; i++)
                    {
                        var cam = DynamicWebcams[i];
                        string key = "Webcam_" + i;
                        settings.MicLevels[key] = cam.AudioLevel;
                        settings.MicStates[key] = cam.State;
                    }
                    
                    settings.MicLevels.Clear();
                    foreach (var kvp in DynamicMicLevels) settings.MicLevels[kvp.Key] = kvp.Value;
                    settings.MicStates.Clear();
                    foreach (var kvp in DynamicMicStates) settings.MicStates[kvp.Key] = kvp.Value;
                    
                    settings.YouTubeStreamKey = TxtStreamKey.Text;
                    settings.SelectedRecordingProfile = (CmbRecordingProfile.SelectedItem as RecordingProfileDefinition)?.DisplayName ?? "MP4 High Quality";
                    settings.RecordingDirectory = TxtRecordingDirectory.Text;

                    if (CmbSrtMode != null) settings.SrtMode = (CmbSrtMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Caller";
                    if (TxtSrtHost != null) settings.SrtHost = TxtSrtHost.Text.Trim();
                    if (TxtSrtPort != null && int.TryParse(TxtSrtPort.Text.Trim(), out int sp) && sp > 0) settings.SrtPort = sp;
                    if (TxtSrtLatency != null && int.TryParse(TxtSrtLatency.Text.Trim(), out int sl) && sl >= 0) settings.SrtLatencyMs = sl;
                    if (TxtSrtPassphrase != null) settings.SrtPassphrase = TxtSrtPassphrase.Text;
                    if (TxtSrtStreamId != null) settings.SrtStreamId = TxtSrtStreamId.Text;
                    if (TxtSrtUrl != null) settings.SrtUrl = TxtSrtUrl.Text.Trim();

                    settings.LogoFilePath = _logoFilePath ?? "";
                    settings.LogoOverlayEnabled = IsLogoOverlayEnabled;
                    if (CmbLogoPosition?.SelectedItem is ComboBoxItem logoPosItem)
                        settings.LogoPositionPreset = logoPosItem.Content?.ToString() ?? "Top-Right";
                    if (SldLogoSize != null) settings.LogoSizePercent = SldLogoSize.Value;
                    if (SldLogoOpacity != null) settings.LogoOpacityPercent = SldLogoOpacity.Value;
                    if (SldLogoX != null) settings.LogoXPercent = SldLogoX.Value;
                    if (SldLogoY != null) settings.LogoYPercent = SldLogoY.Value;

                    if (TxtHtmlSourceUrl != null) settings.HtmlSourceUrl = TxtHtmlSourceUrl.Text.Trim();
                    if (CmbAudioStateHtml != null && CmbAudioStateHtml.SelectedIndex >= 0)
                        settings.AudioStateHtml = (AudioState)CmbAudioStateHtml.SelectedIndex;
                    if (TxtHtmlOverlayUrl != null) settings.HtmlOverlayUrl = TxtHtmlOverlayUrl.Text.Trim();
                    settings.HtmlOverlayEnabled = IsHtmlOverlayEnabled;
                    if (SldHtmlOverlayOpacity != null) settings.HtmlOverlayOpacityPercent = SldHtmlOverlayOpacity.Value;

                    settings.NdiOverlaySourceName = (CmbNdiOverlaySources?.SelectedItem as NdiSourceInfo)?.Name ?? _pendingNdiOverlaySourceName ?? settings.NdiOverlaySourceName;
                    settings.NdiOverlayEnabled = IsNdiOverlayEnabled;
                    if (SldNdiOverlayOpacity != null) settings.NdiOverlayOpacityPercent = SldNdiOverlayOpacity.Value;

                    settings.SourceOrder = SwitcherPanel.Children
                        .OfType<FrameworkElement>()
                        .Select(GetTileIdentifier)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToList();
                });
                
                settings.Save();
            }
            catch { }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Log("Application closing. Releasing DeckLink resources...");
            
            // Save Settings
            SaveCurrentSettings();

            _seekTimer?.Stop();

            // Hide the window immediately so it doesn't freeze on screen
            this.Hide();
            e.Cancel = true;

            // Run the cleanup in the background, then fully exit
            Task.Run(() =>
            {
                try { SrtOut.Stop(); } catch { }
                _syntheticCts?.Cancel();
                foreach (var input in DynamicInputs)
                {
                    if (input.Device != null) input.Device.StopCapture();
                }
                foreach (var cam in DynamicWebcams)
                {
                    cam.Source?.Dispose();
                }
                foreach (var scr in DynamicScreens)
                {
                    scr.Context?.Dispose();
                }
                if (_activeOutput != null) _activeOutput.StopPlayback();
                if (_mediaSource != null) _mediaSource.Dispose();
                if (_youtubeSource != null) _youtubeSource.Dispose();
                try { _ndiSource?.Dispose(); } catch { }
                try { _ndiOverlaySource?.Dispose(); } catch { }
                if (_htmlSource != null) _htmlSource.Dispose();
                if (_htmlOverlaySource != null) _htmlOverlaySource.Dispose();
                _systemAudioSource?.Dispose();
                SystemAudioPlayer.Shutdown();
                Log("Resources released.");
                Environment.Exit(0);
            });
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                StartDesktopCaptureLoop();
                Log("Window loaded. Discovering hardware...");
                
                UpdateMiniPreviewsSynthetic();
                
                var screens = System.Windows.Forms.Screen.AllScreens;
                DynamicScreens.Clear();
                for (int i = 0; i < screens.Length; i++)
                {
                    var scr = screens[i];
                    string name = screens.Length == 1 ? "Desktop" : $"Screen {i + 1}";
                    var vm = new ScreenInputViewModel(i, name, scr);
                    vm.PreviewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    DynamicScreens.Add(vm);

                    var template = (DataTemplate)FindResource("ScreenTileTemplate");
                    var tile = (UIElement)template.LoadContent();
                    if (tile is FrameworkElement fe) fe.DataContext = vm;
                    SwitcherPanel.Children.Add(tile);
                }
                if (DynamicScreens.Count > 0 && _activeScreen == null)
                {
                    _activeScreen = DynamicScreens[0];
                }
                
                List<DeckLinkDeviceInfo> devices = new List<DeckLinkDeviceInfo>();
                devices.Add(new DeckLinkDeviceInfo { DisplayName = "None", Index = -1 });

                Log("Discovering devices in background...");
                int physicalCount = 0;
                var micDevices = new System.Collections.Generic.List<MicDeviceInfo>();
                var webcamDevices = new System.Collections.Generic.List<WebcamDeviceInfo>();
                
                await Task.Run(() => 
                {
                    IDeckLinkIterator deckLinkIterator = null;
                    try
                    {
                        deckLinkIterator = new CDeckLinkIterator();
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to create DeckLinkIterator: " + ex.ToString());
                    }

                    if (deckLinkIterator != null)
                    {
                        while (true)
                        {
                            try
                            {
                                deckLinkIterator.Next(out IDeckLink deckLink);
                                if (deckLink == null) break;

                                deckLink.GetModelName(out string modelName);
                                string uniqueName = $"{modelName} (Port {physicalCount + 1})";
                                devices.Add(new DeckLinkDeviceInfo { DisplayName = uniqueName, Index = physicalCount });
                                physicalCount++;
                            }
                            catch (Exception ex)
                            {
                                Log("Error enumerating DeckLink device: " + ex.Message);
                                break;
                            }
                        }
                    }
                    
                    micDevices.AddRange(LocalAudioInput.GetDevices());
                    webcamDevices.AddRange(WebcamSource.GetDevices());
                });

                if (physicalCount == 0)
                {
                    Log("No physical DeckLink devices found. 'None' option is available.");
                    TxtStatus.Text = "No hardware detected. 'None' available.";
                }
                else
                {
                    Log($"Found {physicalCount} physical DeckLink device(s).");
                    TxtStatus.Text = "Ready to assign.";
                }

                CmbOutput.ItemsSource = new List<DeckLinkDeviceInfo>(devices);

                var settings = AppSettings.Load();
                
                if (settings.WindowWidth > 0 && !double.IsNaN(settings.WindowWidth)) this.Width = settings.WindowWidth;
                if (settings.WindowHeight > 0 && !double.IsNaN(settings.WindowHeight)) this.Height = settings.WindowHeight;
                if (!double.IsNaN(settings.WindowLeft)) this.Left = settings.WindowLeft;
                if (!double.IsNaN(settings.WindowTop)) this.Top = settings.WindowTop;
                if (settings.WindowMaximized) this.WindowState = System.Windows.WindowState.Maximized;

                int FindDeviceIndex(string name, int defaultIndex)
                {
                    if (string.IsNullOrEmpty(name)) return defaultIndex < devices.Count ? defaultIndex : 0;
                    var idx = devices.FindIndex(d => d.DisplayName == name);
                    return idx >= 0 ? idx : (defaultIndex < devices.Count ? defaultIndex : 0);
                }

                if (physicalCount > 0)
                {
                    CmbOutput.SelectedIndex = FindDeviceIndex(settings.OutputDevice, 1);
                }
                else
                {
                    CmbOutput.SelectedIndex = 0;
                }
                
                CmbOutput.SelectionChanged += (s, e) => { if (IsLoaded) BtnApplySettings.IsEnabled = true; };
                
                InputSelectorsControl.ItemsSource = InputSelectors;
                InputSelectors.Clear();
                int numInputs = Math.Max(0, physicalCount - 1);
                if (numInputs == 0) numInputs = 1; // Default to at least 1 selector if there are cards
                
                var availableDevices = new System.Collections.ObjectModel.ObservableCollection<DeckLinkDeviceInfo>(devices);
                for (int i = 0; i < numInputs; i++)
                {
                    string label = "IN" + (i + 1);
                    var vm = new InputSelectionViewModel { Label = label, AvailableDevices = availableDevices };
                    
                    int defaultIndex = 0;
                    if (physicalCount > i + 2) defaultIndex = i + 2; // e.g. Input 1 gets device 2, Input 2 gets device 3 (device 1 is output)
                    
                    if (settings.InputDevices.ContainsKey(label))
                    {
                        vm.SelectedDevice = availableDevices[FindDeviceIndex(settings.InputDevices[label], defaultIndex)];
                    }
                    else
                    {
                        vm.SelectedDevice = availableDevices[defaultIndex];
                    }

                    if (settings.InputVideoFormats.ContainsKey(label) && vm.AvailableFormats.Contains(settings.InputVideoFormats[label]))
                    {
                        vm.SelectedFormat = settings.InputVideoFormats[label];
                    }

                    vm.PropertyChanged += (s, e) => { 
                        if (IsLoaded && (e.PropertyName == nameof(InputSelectionViewModel.SelectedDevice) || e.PropertyName == nameof(InputSelectionViewModel.SelectedFormat))) 
                            BtnApplySettings.IsEnabled = true; 
                    };
                    InputSelectors.Add(vm);
                }
                SldPgmAudio.Value = settings.AudioLevelPgm;
                SldColorBarsAudio.Value = settings.AudioLevelColorBars;
                SldMediaAudio.Value = settings.AudioLevelMedia;
                
                CmbMatteColor.SelectedIndex = settings.MatteColorIndex;
                ChkSystemAudioOut.IsChecked = settings.SystemAudioMonitorEnabled;
                ChkLoopMedia.IsChecked = settings.LoopMediaEnabled;
                CmbAudioStateColorBars.SelectedIndex = (int)settings.AudioStateColorBars;
                CmbAudioStateMedia.SelectedIndex = (int)settings.AudioStateMedia;
                if (!string.IsNullOrEmpty(settings.YouTubeSourceUrl))
                    TxtYouTubeUrl.Text = settings.YouTubeSourceUrl;
                SldYouTubeAudio.Value = settings.AudioLevelYouTube;
                YouTubeAudioLevel = (float)settings.AudioLevelYouTube;
                CmbAudioStateYouTube.SelectedIndex = (int)settings.AudioStateYouTube;
                StateYouTube = settings.AudioStateYouTube;
                SldNdiAudio.Value = settings.AudioLevelNdi;
                NdiAudioLevel = (float)settings.AudioLevelNdi;
                CmbAudioStateNdi.SelectedIndex = (int)settings.AudioStateNdi;
                StateNdi = settings.AudioStateNdi;
                _pendingNdiSourceName = settings.NdiSourceName;
                if (SldYouTubeAudioDelay != null)
                {
                    SldYouTubeAudioDelay.Value = settings.YouTubeAudioDelayMs;
                    if (TxtYouTubeAudioDelay != null) TxtYouTubeAudioDelay.Text = FormatDelayText(settings.YouTubeAudioDelayMs);
                }
                if (_youtubeSource != null) _youtubeSource.AudioDelayMs = settings.YouTubeAudioDelayMs;
                InvertDeckLinkRight = settings.InvertDeckLinkRightChannel;
                if (ChkInvertDeckLinkR != null) ChkInvertDeckLinkR.IsChecked = settings.InvertDeckLinkRightChannel;

                // System Audio Setup
                var renderDevices = SystemAudioSource.GetRenderDevices();
                CmbSystemAudioDevice.ItemsSource = renderDevices;
                int sysDevIdx = renderDevices.FindIndex(d => d.DeviceId == settings.SelectedSystemAudioDevice);
                CmbSystemAudioDevice.SelectedIndex = sysDevIdx >= 0 ? sysDevIdx : 0;

                SldSystemAudio.Value = settings.AudioLevelSystemAudio;
                SystemAudioLevel = (float)settings.AudioLevelSystemAudio;
                CmbAudioStateSystemAudio.SelectedIndex = (int)settings.AudioStateSystemAudio;
                StateSystemAudio = settings.AudioStateSystemAudio;

                _systemAudioSource.OnAudioLevelArrived = (l, r) =>
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        double levelL = l * SystemAudioLevel;
                        double levelR = r * SystemAudioLevel;
                        AudioBarSysL.Value = levelL;
                        AudioBarSysR.Value = levelR;

                        bool isSysAudioActive = (StateSystemAudio == AudioState.ON) || (StateSystemAudio == AudioState.AFV && _activeSourceType == 4);
                        if (isSysAudioActive && _activeSourceType == 4)
                        {
                            AudioOutputL.Value = levelL * PgmAudioLevel;
                            AudioOutputR.Value = levelR * PgmAudioLevel;
                        }
                    });
                };

                string? initSysDevId = (CmbSystemAudioDevice.SelectedItem as RenderDeviceInfo)?.DeviceId;
                _systemAudioSource.Start(initSysDevId);
                
                // Dynamic Webcams
                DynamicWebcams.Clear();
                var linkedMicDeviceIds = new System.Collections.Generic.HashSet<string>();

                for (int i = 0; i < webcamDevices.Count; i++)
                {
                    var devInfo = webcamDevices[i];
                    string displayName = webcamDevices.Count == 1 ? "Webcam" : $"Webcam {i + 1}";
                    var vm = new WebcamInputViewModel(displayName, devInfo.Name, i);
                    vm.PreviewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

                    string levelKey = "Webcam_" + i;
                    if (settings.MicLevels.ContainsKey(levelKey)) vm.AudioLevel = settings.MicLevels[levelKey];
                    if (settings.MicStates.ContainsKey(levelKey)) vm.AudioStateIndex = (int)settings.MicStates[levelKey];

                    var src = new WebcamSource();
                    vm.Source = src;

                    src.OnPreviewBufferUpdated = (buf) =>
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            vm.PreviewBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                            if (_activeSourceType == 5 && _activeWebcam == vm && _bmpOutput != null)
                            {
                                _bmpOutput.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                            }
                        });
                    };

                    src.OnPgmBufferUpdated = (buf1080) =>
                    {
                        if (_activeSourceType == 5 && _activeWebcam == vm)
                        {
                            lock (_pgmLock)
                            {
                                if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                                    _latestPgmFrame = new byte[1920 * 1080 * 4];
                                Array.Copy(buf1080, _latestPgmFrame, _latestPgmFrame.Length);
                            }
                        }
                    };

                    src.OnAudioLevelArrived = (l, r) =>
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            vm.AudioL = (int)l;
                            vm.AudioR = (int)r;
                        });
                    };

                    if (devInfo.Descriptor != null)
                    {
                        _ = src.StartAsync(devInfo.Descriptor);
                    }

                    string? linkedMic = FindWebcamAudioDevice(devInfo.Name, micDevices);
                    if (!string.IsNullOrEmpty(linkedMic))
                    {
                        linkedMicDeviceIds.Add(linkedMic);
                        src.StartAudio(linkedMic, displayName);
                    }

                    DynamicWebcams.Add(vm);

                    var template = (DataTemplate)FindResource("WebcamTileTemplate");
                    var tile = (UIElement)template.LoadContent();
                    if (tile is FrameworkElement fe) fe.DataContext = vm;
                    SwitcherPanel.Children.Add(tile);
                }

                if (DynamicWebcams.Count > 0 && _activeWebcam == null)
                {
                    _activeWebcam = DynamicWebcams[0];
                }

                _activeMics.Clear();
                foreach (var device in micDevices)
                {
                    if (device.DeviceNumber < 0) continue; // Skip 'None'
                    if (linkedMicDeviceIds.Contains(device.DeviceId)) continue; // Managed directly on Webcam tile
                    string key = "Mic_" + device.DeviceNumber;
                    
                    double level = 1.0;
                    if (settings.MicLevels.ContainsKey(key)) level = settings.MicLevels[key];
                    AudioState state = AudioState.ON;
                    if (settings.MicStates.ContainsKey(key)) state = settings.MicStates[key];
                    
                    DynamicMicLevels[key] = (float)level;
                    DynamicMicStates[key] = state;
                    
                    var pnl = CreateMicPanel(device, key, level, state);
                    var wrapper = new System.Windows.Controls.Border();
                    wrapper.Tag = key;
                    wrapper.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "MixerPanel");
                    wrapper.Child = pnl;
                    SwitcherPanel.Children.Add(wrapper);
                    
                    var input = new LocalAudioInput(device.DeviceId, key);
                    var barL = (System.Windows.Controls.ProgressBar)pnl.FindName("BarL_" + device.DeviceNumber);
                    var barR = (System.Windows.Controls.ProgressBar)pnl.FindName("BarR_" + device.DeviceNumber);
                    input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { if (barL != null) barL.Value = l; if (barR != null) barR.Value = r; }); };
                    input.Start();
                    _activeMics.Add(input);
                }
                
                TxtStreamKey.Text = settings.YouTubeStreamKey;
                YtStreamer.OnLog = Log;
                LclRecorder.OnLog = Log;
                NdiOut.OnLog = Log;
                SrtOut.OnLog = Log;
                SrtOut.OnStateChanged = (isStreaming) => Application.Current.Dispatcher.BeginInvoke(() => UpdateSrtUiState(isStreaming));
                TxtNdiName.Text = string.IsNullOrWhiteSpace(settings.NdiStreamName) ? "DeckLink Switcher - Program" : settings.NdiStreamName;

                // Initialize SRT UI
                _isUpdatingSrtUrl = true;
                try
                {
                    if (CmbSrtMode != null)
                    {
                        foreach (ComboBoxItem item in CmbSrtMode.Items)
                        {
                            if (string.Equals(item.Content?.ToString(), settings.SrtMode, StringComparison.OrdinalIgnoreCase))
                            {
                                CmbSrtMode.SelectedItem = item;
                                break;
                            }
                        }
                    }
                    if (TxtSrtHost != null) TxtSrtHost.Text = string.IsNullOrWhiteSpace(settings.SrtHost) ? "127.0.0.1" : settings.SrtHost;
                    if (TxtSrtPort != null) TxtSrtPort.Text = settings.SrtPort > 0 ? settings.SrtPort.ToString() : "9000";
                    if (TxtSrtLatency != null) TxtSrtLatency.Text = settings.SrtLatencyMs >= 0 ? settings.SrtLatencyMs.ToString() : "200";
                    if (TxtSrtPassphrase != null) TxtSrtPassphrase.Text = settings.SrtPassphrase ?? "";
                    if (TxtSrtStreamId != null) TxtSrtStreamId.Text = settings.SrtStreamId ?? "";
                    if (TxtSrtUrl != null)
                    {
                        TxtSrtUrl.Text = !string.IsNullOrWhiteSpace(settings.SrtUrl) ? settings.SrtUrl : BuildSrtUrl();
                    }
                }
                finally
                {
                    _isUpdatingSrtUrl = false;
                }
                
                // Initialize recording profiles
                var profiles = new[]
                {
                    new RecordingProfileDefinition("XDCAM HD422", ".mxf", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a pcm_s16le -ar 48000 -ac 2", null, "XDCAM_HD422"),
                    new RecordingProfileDefinition("XDCAM Sony Compatible", ".mxf", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -mpv_flags +strict_gop -sc_threshold 1000000000 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -non_linear_quant 1 -tag:v xd5c -c:a pcm_s24le -ar 48000 -signal_standard smpte274m", null, "xdcamhd422_sony_compatible", useDualMonoAudio: true),
                    new RecordingProfileDefinition("MP4 High Quality", ".mp4", "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -profile:v high -c:a aac -b:a 192k -ar 48000 -ac 2", null, "MP4_High"),
                    new RecordingProfileDefinition("MP4 Low Bitrate", ".mp4", "-c:v libx264 -preset veryfast -crf 24 -pix_fmt yuv420p -profile:v high -c:a aac -b:a 128k -ar 48000 -ac 2", null, "MP4_Low"),
                    new RecordingProfileDefinition("TS H.264 High Quality", ".ts", "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -profile:v high -c:a aac -b:a 192k -ar 48000 -ac 2", null, "TS_H264_High"),
                    new RecordingProfileDefinition("TS H.264 Low Bitrate", ".ts", "-c:v libx264 -preset veryfast -crf 25 -pix_fmt yuv420p -profile:v high -c:a aac -b:a 128k -ar 48000 -ac 2", null, "TS_H264_Low"),
                    new RecordingProfileDefinition("TS MPEG-2 4:2:2 50M", ".ts", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a mp2 -b:a 384k -ar 48000 -ac 2", null, "TS_MPEG2_50M"),
                    new RecordingProfileDefinition("ProRes Proxy (Small)", ".mov", "-c:v prores_ks -profile:v 0 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 400 -c:a pcm_s16le -ar 48000", null, "ProRes_Proxy"),
                    new RecordingProfileDefinition("ProRes LT (Light)", ".mov", "-c:v prores_ks -profile:v 1 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1000 -c:a pcm_s16le -ar 48000", null, "ProRes_LT"),
                    new RecordingProfileDefinition("ProRes 422 (Medium)", ".mov", "-c:v prores_ks -profile:v 2 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1600 -c:a pcm_s16le -ar 48000", null, "ProRes_422"),
                    new RecordingProfileDefinition("ProRes 422 HQ (High)", ".mov", "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 2400 -c:a pcm_s16le -ar 48000", null, "ProRes_422_HQ"),
                    new RecordingProfileDefinition("MP4 4K H.264 (NVENC)", ".mp4", "-c:v h264_nvenc -preset p4 -cq 22 -pix_fmt yuv420p -r 25 -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_H264_NVENC"),
                    new RecordingProfileDefinition("MP4 4K HEVC (NVENC)", ".mp4", "-c:v hevc_nvenc -preset p4 -cq 24 -pix_fmt yuv420p -r 25 -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_HEVC_NVENC"),
                    new RecordingProfileDefinition("MP4 4K H.264 (CPU)", ".mp4", "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -profile:v high -r 25 -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_H264_CPU"),
                    new RecordingProfileDefinition("MP4 4K HEVC (CPU)", ".mp4", "-c:v libx265 -preset veryfast -crf 24 -pix_fmt yuv420p -r 25 -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_HEVC_CPU"),
                    new RecordingProfileDefinition("ProRes 4K 422", ".mov", "-c:v prores_ks -profile:v 2 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1600 -c:a pcm_s16le -ar 48000", null, "ProRes_4K_422"),
                    new RecordingProfileDefinition("ProRes 4K 422 HQ", ".mov", "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 2400 -c:a pcm_s16le -ar 48000", null, "ProRes_4K_422_HQ"),
                    new RecordingProfileDefinition("DNxHD 36 (Proxy)", ".mxf", "-c:v dnxhd -b:v 36M -pix_fmt yuv422p -c:a pcm_s16le -ar 48000", null, "DNxHD_36"),
                    new RecordingProfileDefinition("DNxHD 120 (Standard)", ".mxf", "-c:v dnxhd -b:v 120M -pix_fmt yuv422p -c:a pcm_s16le -ar 48000", null, "DNxHD_120"),
                    new RecordingProfileDefinition("DNxHD 185 (High)", ".mxf", "-c:v dnxhd -b:v 185M -pix_fmt yuv422p -c:a pcm_s16le -ar 48000", null, "DNxHD_185"),
                    new RecordingProfileDefinition("DNxHD 185x (10-bit)", ".mxf", "-c:v dnxhd -b:v 185M -pix_fmt yuv422p10le -c:a pcm_s16le -ar 48000", null, "DNxHD_185x")
                };

                foreach (var p in profiles)
                {
                    CmbRecordingProfile.Items.Add(p);
                    if (p.DisplayName == settings.SelectedRecordingProfile ||
                        p.FileNameSuffix == settings.SelectedRecordingProfile ||
                        (p.DisplayName == "XDCAM Sony Compatible" && settings.SelectedRecordingProfile == "xdcamhd422_sony_compatible"))
                    {
                        CmbRecordingProfile.SelectedItem = p;
                    }
                }
                if (CmbRecordingProfile.SelectedIndex == -1 && CmbRecordingProfile.Items.Count > 0)
                {
                    CmbRecordingProfile.SelectedIndex = 0;
                }
                
                TxtRecordingDirectory.Text = settings.RecordingDirectory;

                // Restore PGM Logo Overlay settings
                _isUpdatingLogoUi = true;
                try
                {
                    LogoSizePercent = settings.LogoSizePercent > 0 ? Math.Clamp(settings.LogoSizePercent, 3.0, 50.0) : 15.0;
                    LogoOpacityPercent = settings.LogoOpacityPercent > 0 ? Math.Clamp(settings.LogoOpacityPercent, 10.0, 100.0) : 100.0;
                    LogoXPercent = Math.Clamp(settings.LogoXPercent, 0.0, 100.0);
                    LogoYPercent = Math.Clamp(settings.LogoYPercent, 0.0, 100.0);

                    if (SldLogoSize != null) SldLogoSize.Value = LogoSizePercent;
                    if (TxtLogoSizeVal != null) TxtLogoSizeVal.Text = $"{(int)Math.Round(LogoSizePercent)}%";
                    if (SldLogoOpacity != null) SldLogoOpacity.Value = LogoOpacityPercent;
                    if (TxtLogoOpacityVal != null) TxtLogoOpacityVal.Text = $"{(int)Math.Round(LogoOpacityPercent)}%";
                    if (SldLogoX != null) SldLogoX.Value = LogoXPercent;
                    if (TxtLogoXVal != null) TxtLogoXVal.Text = $"{(int)Math.Round(LogoXPercent)}%";
                    if (SldLogoY != null) SldLogoY.Value = LogoYPercent;
                    if (TxtLogoYVal != null) TxtLogoYVal.Text = $"{(int)Math.Round(LogoYPercent)}%";

                    if (CmbLogoPosition != null && !string.IsNullOrWhiteSpace(settings.LogoPositionPreset))
                    {
                        foreach (ComboBoxItem item in CmbLogoPosition.Items)
                        {
                            if (string.Equals(item.Content?.ToString(), settings.LogoPositionPreset, StringComparison.OrdinalIgnoreCase))
                            {
                                CmbLogoPosition.SelectedItem = item;
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(settings.LogoFilePath) && File.Exists(settings.LogoFilePath))
                    {
                        LoadLogoFromFile(settings.LogoFilePath);
                    }

                    SetLogoOverlayActiveState(settings.LogoOverlayEnabled && CurrentLogoOverlay != null);

                    if (!string.IsNullOrEmpty(settings.HtmlSourceUrl) && TxtHtmlSourceUrl != null)
                        TxtHtmlSourceUrl.Text = settings.HtmlSourceUrl;
                    if (CmbAudioStateHtml != null)
                        CmbAudioStateHtml.SelectedIndex = (int)settings.AudioStateHtml;
                    StateHtml = settings.AudioStateHtml;

                    if (!string.IsNullOrEmpty(settings.HtmlOverlayUrl) && TxtHtmlOverlayUrl != null)
                        TxtHtmlOverlayUrl.Text = settings.HtmlOverlayUrl;
                    _pendingHtmlOverlayEnabled = settings.HtmlOverlayEnabled;
                    HtmlOverlayOpacityPercent = settings.HtmlOverlayOpacityPercent > 0 ? Math.Clamp(settings.HtmlOverlayOpacityPercent, 10.0, 100.0) : 100.0;
                    if (SldHtmlOverlayOpacity != null) SldHtmlOverlayOpacity.Value = HtmlOverlayOpacityPercent;
                    if (TxtHtmlOverlayOpacityVal != null) TxtHtmlOverlayOpacityVal.Text = $"{(int)Math.Round(HtmlOverlayOpacityPercent)}%";
                    SetHtmlOverlayActiveState(false);

                    _pendingNdiOverlaySourceName = settings.NdiOverlaySourceName;
                    NdiOverlayOpacityPercent = settings.NdiOverlayOpacityPercent > 0 ? Math.Clamp(settings.NdiOverlayOpacityPercent, 10.0, 100.0) : 100.0;
                    if (SldNdiOverlayOpacity != null) SldNdiOverlayOpacity.Value = NdiOverlayOpacityPercent;
                    if (TxtNdiOverlayOpacityVal != null) TxtNdiOverlayOpacityVal.Text = $"{(int)Math.Round(NdiOverlayOpacityPercent)}%";
                    SetNdiOverlayActiveState(false);
                }
                finally
                {
                    _isUpdatingLogoUi = false;
                }
                
                ApplySavedSourceOrder();

                TxtStatus.Text = "Ready to assign.";
                Log("MainWindow_Loaded completed successfully.");
            }
            catch (Exception ex)
            {
                Log("MainWindow_Loaded error: " + ex.ToString());
            }
            finally
            {
                _isLoadingSettings = false;
            }

            _ = AutoStartSavedSourcesAsync();
        }

        private async Task AutoStartSavedSourcesAsync()
        {
            var tasks = new List<Task>();

            string htmlUrl = TxtHtmlSourceUrl?.Text?.Trim() ?? string.Empty;
            if (_htmlSource != null && !string.IsNullOrEmpty(htmlUrl))
            {
                tasks.Add(StartHtmlSourceAtStartupAsync(htmlUrl));
            }

            string htmlOverlayUrl = TxtHtmlOverlayUrl?.Text?.Trim() ?? string.Empty;
            if (_htmlOverlaySource != null && !string.IsNullOrEmpty(htmlOverlayUrl))
            {
                bool restoreOverlayEnabled = _pendingHtmlOverlayEnabled;
                tasks.Add(StartHtmlOverlayAtStartupAsync(htmlOverlayUrl, restoreOverlayEnabled));
            }

            string youtubeUrl = TxtYouTubeUrl?.Text?.Trim() ?? string.Empty;
            if (_youtubeSource != null && !string.IsNullOrEmpty(youtubeUrl))
            {
                tasks.Add(StartYouTubeAtStartupAsync(youtubeUrl));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        private async Task StartHtmlSourceAtStartupAsync(string url)
        {
            try
            {
                if (BtnDisconnectHtmlSource != null) BtnDisconnectHtmlSource.IsEnabled = true;
                await _htmlSource!.StartAsync(url);
                UpdateHtmlSourceMuteState();
                Log($"Auto-started HTML Source: {url}");
            }
            catch (Exception ex)
            {
                Log($"Auto-start HTML Source error: {ex.Message}");
            }
        }

        private async Task StartHtmlOverlayAtStartupAsync(string url, bool restoreEnabled)
        {
            try
            {
                await _htmlOverlaySource!.StartAsync(url);
                SetHtmlOverlayActiveState(restoreEnabled);
                Log($"Auto-started HTML Overlay: {url} (enabled={restoreEnabled})");
            }
            catch (Exception ex)
            {
                Log($"Auto-start HTML Overlay error: {ex.Message}");
            }
        }

        private async Task StartYouTubeAtStartupAsync(string url)
        {
            try
            {
                if (BtnConnectYouTube != null) BtnConnectYouTube.IsEnabled = false;
                if (BtnDisconnectYouTube != null) BtnDisconnectYouTube.IsEnabled = true;
                await _youtubeSource!.StartAsync(url);
                Log($"Auto-started YouTube Source: {url}");
            }
            catch (Exception ex)
            {
                Log($"Auto-start YouTube Source error: {ex.Message}");
            }
        }

        #region PGM Logo Overlay

        private void BtnSelectLogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select PGM Logo Image",
                Filter = "Image Files (*.png;*.webp;*.bmp;*.jpg;*.jpeg)|*.png;*.webp;*.bmp;*.jpg;*.jpeg|PNG Files (*.png)|*.png|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                if (LoadLogoFromFile(dlg.FileName))
                {
                    SetLogoOverlayActiveState(true);
                    SaveCurrentSettings();
                }
            }
        }

        private void BtnToggleLogo_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentLogoOverlay == null && !IsLogoOverlayEnabled)
            {
                BtnSelectLogo_Click(sender, e);
                return;
            }
            SetLogoOverlayActiveState(!IsLogoOverlayEnabled);
            SaveCurrentSettings();
        }

        private void SetLogoOverlayActiveState(bool enabled)
        {
            IsLogoOverlayEnabled = enabled && CurrentLogoOverlay != null;
            _isUpdatingLogoUi = true;
            try
            {
                if (BtnToggleLogo != null)
                {
                    BtnToggleLogo.Content = IsLogoOverlayEnabled ? "LOGO: ON" : "LOGO: OFF";
                    BtnToggleLogo.Background = IsLogoOverlayEnabled
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x7d, 0x32))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x42, 0x42));
                }
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }
            UpdateLogoPreviewElement();
        }

        private bool LoadLogoFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                byte[] fileBytes = File.ReadAllBytes(filePath);
                using var ms1 = new MemoryStream(fileBytes);
                using var loadedBmp = new System.Drawing.Bitmap(ms1);
                var clonedBmp = new System.Drawing.Bitmap(loadedBmp.Width, loadedBmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(clonedBmp))
                {
                    g.DrawImage(loadedBmp, 0, 0, loadedBmp.Width, loadedBmp.Height);
                }

                var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms2 = new MemoryStream(fileBytes))
                {
                    bmpImg.BeginInit();
                    bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmpImg.StreamSource = ms2;
                    bmpImg.EndInit();
                    bmpImg.Freeze();
                }

                _rawLogoBitmap?.Dispose();
                _rawLogoBitmap = clonedBmp;
                _logoPreviewImageSource = bmpImg;
                _logoFilePath = filePath;

                if (TxtLogoFileName != null)
                {
                    TxtLogoFileName.Text = Path.GetFileName(filePath);
                    TxtLogoFileName.ToolTip = filePath;
                }

                RebuildPreparedLogo();
                return true;
            }
            catch (Exception ex)
            {
                Log("LoadLogoFromFile error: " + ex.Message);
                System.Windows.MessageBox.Show("Failed to load logo image: " + ex.Message, "Logo Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private unsafe void RebuildPreparedLogo()
        {
            if (_rawLogoBitmap == null || _rawLogoBitmap.Width <= 0 || _rawLogoBitmap.Height <= 0)
            {
                CurrentLogoOverlay = null;
                UpdateLogoPreviewElement();
                return;
            }

            int w = Math.Max(2, (int)Math.Round(1920.0 * (Math.Clamp(LogoSizePercent, 3.0, 50.0) / 100.0)));
            if ((w & 1) != 0) w++;
            if (w > 1920) w = 1920;

            int h = Math.Max(2, (int)Math.Round(w * ((double)_rawLogoBitmap.Height / _rawLogoBitmap.Width)));
            if (h > 1080)
            {
                h = 1080;
                w = Math.Max(2, (int)Math.Round(h * ((double)_rawLogoBitmap.Width / _rawLogoBitmap.Height))) & ~1;
            }

            using var scaled = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(scaled))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(_rawLogoBitmap, new System.Drawing.Rectangle(0, 0, w, h));
            }

            int totalPixels = w * h;
            byte[] yArr = new byte[totalPixels];
            byte[] uArr = new byte[totalPixels];
            byte[] vArr = new byte[totalPixels];
            byte[] aArr = new byte[totalPixels];

            var bmpData = scaled.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)bmpData.Scan0;
                int stride = bmpData.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = scan0 + y * stride;
                    int rowOffset = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4 + 0];
                        byte gVal = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];
                        byte a = row[x * 4 + 3];

                        int yVal = ((66 * r + 129 * gVal + 25 * b + 128) >> 8) + 16;
                        int uVal = ((-38 * r - 74 * gVal + 112 * b + 128) >> 8) + 128;
                        int vVal = ((112 * r - 94 * gVal - 18 * b + 128) >> 8) + 128;

                        if (yVal < 16) yVal = 16; else if (yVal > 235) yVal = 235;
                        if (uVal < 16) uVal = 16; else if (uVal > 240) uVal = 240;
                        if (vVal < 16) vVal = 16; else if (vVal > 240) vVal = 240;

                        int idx = rowOffset + x;
                        yArr[idx] = (byte)yVal;
                        uArr[idx] = (byte)uVal;
                        vArr[idx] = (byte)vVal;
                        aArr[idx] = a;
                    }
                }
            }
            finally
            {
                scaled.UnlockBits(bmpData);
            }

            CurrentLogoOverlay = new PreparedLogoOverlay
            {
                Width = w,
                Height = h,
                Y = yArr,
                U = uArr,
                V = vArr,
                A = aArr
            };

            UpdateLogoPreviewElement();
        }

        private void UpdateLogoPreviewElement()
        {
            if (PreviewLogoOverlay == null) return;
            var ov = CurrentLogoOverlay;
            if (!IsLogoOverlayEnabled || _logoPreviewImageSource == null || ov == null)
            {
                PreviewLogoOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            double w1080 = ov.Width;
            double h1080 = ov.Height;
            double x1080 = (1920.0 - w1080) * (Math.Clamp(LogoXPercent, 0.0, 100.0) / 100.0);
            double y1080 = (1080.0 - h1080) * (Math.Clamp(LogoYPercent, 0.0, 100.0) / 100.0);

            PreviewLogoOverlay.Source = _logoPreviewImageSource;
            PreviewLogoOverlay.Width = w1080 * 0.25;
            PreviewLogoOverlay.Height = h1080 * 0.25;
            System.Windows.Controls.Canvas.SetLeft(PreviewLogoOverlay, x1080 * 0.25);
            System.Windows.Controls.Canvas.SetTop(PreviewLogoOverlay, y1080 * 0.25);
            PreviewLogoOverlay.Opacity = Math.Clamp(LogoOpacityPercent / 100.0, 0.0, 1.0);
            PreviewLogoOverlay.Visibility = Visibility.Visible;
        }

        private void CmbLogoPosition_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingLogoUi || !IsLoaded) return;
            string preset = (CmbLogoPosition.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Top-Right";
            if (preset == "Custom")
            {
                SaveCurrentSettings();
                return;
            }

            double targetX = LogoXPercent;
            double targetY = LogoYPercent;
            switch (preset)
            {
                case "Top-Right":    targetX = 96.0; targetY = 4.0; break;
                case "Top-Left":     targetX = 4.0;  targetY = 4.0; break;
                case "Bottom-Right": targetX = 96.0; targetY = 94.0; break;
                case "Bottom-Left":  targetX = 4.0;  targetY = 94.0; break;
                case "Center":       targetX = 50.0; targetY = 50.0; break;
            }

            _isUpdatingLogoUi = true;
            try
            {
                LogoXPercent = targetX;
                LogoYPercent = targetY;
                if (SldLogoX != null) SldLogoX.Value = targetX;
                if (TxtLogoXVal != null) TxtLogoXVal.Text = $"{(int)Math.Round(targetX)}%";
                if (SldLogoY != null) SldLogoY.Value = targetY;
                if (TxtLogoYVal != null) TxtLogoYVal.Text = $"{(int)Math.Round(targetY)}%";
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }

            UpdateLogoPreviewElement();
            SaveCurrentSettings();
        }

        private void SldLogoSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LogoSizePercent = e.NewValue;
            if (TxtLogoSizeVal != null) TxtLogoSizeVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            RebuildPreparedLogo();
            SaveCurrentSettings();
        }

        private void SldLogoOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            LogoOpacityPercent = e.NewValue;
            if (TxtLogoOpacityVal != null) TxtLogoOpacityVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            UpdateLogoPreviewElement();
            SaveCurrentSettings();
        }

        private void SldLogoPos_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SldLogoX != null) LogoXPercent = SldLogoX.Value;
            if (SldLogoY != null) LogoYPercent = SldLogoY.Value;
            if (TxtLogoXVal != null) TxtLogoXVal.Text = $"{(int)Math.Round(LogoXPercent)}%";
            if (TxtLogoYVal != null) TxtLogoYVal.Text = $"{(int)Math.Round(LogoYPercent)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;

            if (CmbLogoPosition != null)
            {
                _isUpdatingLogoUi = true;
                try
                {
                    foreach (ComboBoxItem item in CmbLogoPosition.Items)
                    {
                        if (string.Equals(item.Content?.ToString(), "Custom", StringComparison.OrdinalIgnoreCase))
                        {
                            CmbLogoPosition.SelectedItem = item;
                            break;
                        }
                    }
                }
                finally
                {
                    _isUpdatingLogoUi = false;
                }
            }

            UpdateLogoPreviewElement();
            SaveCurrentSettings();
        }

        private static unsafe void BlendPreparedRegionUyvy(byte* uyvyBuffer, int startX, int startY, int boxW, int boxH, byte[] yArr, byte[] uArr, byte[] vArr, byte[] aArr, double opacityPercent)
        {
            if (boxW <= 0 || boxH <= 0) return;
            int opacityScale = (int)Math.Round(Math.Clamp(opacityPercent, 0.0, 100.0) * 255.0 / 100.0);
            if (opacityScale <= 0) return;

            startX &= ~1;
            if (startX < 0) startX = 0;
            if (startY < 0) startY = 0;
            if (startX + boxW > 1920) startX = (1920 - boxW) & ~1;
            if (startY + boxH > 1080) startY = 1080 - boxH;

            fixed (byte* pY = yArr)
            fixed (byte* pU = uArr)
            fixed (byte* pV = vArr)
            fixed (byte* pA = aArr)
            {
                for (int ly = 0; ly < boxH; ly++)
                {
                    byte* dstRow = uyvyBuffer + ((startY + ly) * 1920 + startX) * 2;
                    int rowIdx = ly * boxW;
                    for (int lx = 0; lx + 1 < boxW; lx += 2)
                    {
                        int idx0 = rowIdx + lx;
                        int idx1 = idx0 + 1;
                        int a0 = (pA[idx0] * opacityScale + 127) / 255;
                        int a1 = (pA[idx1] * opacityScale + 127) / 255;
                        if ((a0 | a1) == 0) continue;

                        int d = lx * 2;
                        if (a0 > 0)
                        {
                            dstRow[d + 1] = (byte)((pY[idx0] * a0 + dstRow[d + 1] * (255 - a0) + 127) / 255);
                        }
                        if (a1 > 0)
                        {
                            dstRow[d + 3] = (byte)((pY[idx1] * a1 + dstRow[d + 3] * (255 - a1) + 127) / 255);
                        }

                        // Alpha-weighted chroma so transparent neighbours don't desaturate/fringe glyph edges
                        int wSum = pA[idx0] + pA[idx1];
                        int uLogo, vLogo;
                        if (wSum > 0)
                        {
                            uLogo = (pU[idx0] * pA[idx0] + pU[idx1] * pA[idx1]) / wSum;
                            vLogo = (pV[idx0] * pA[idx0] + pV[idx1] * pA[idx1]) / wSum;
                        }
                        else
                        {
                            uLogo = 128; vLogo = 128;
                        }
                        int aChroma = (a0 + a1 + 2 * Math.Max(a0, a1)) >> 2;
                        if (aChroma > 0)
                        {
                            dstRow[d + 0] = (byte)((uLogo * aChroma + dstRow[d + 0] * (255 - aChroma) + 127) / 255);
                            dstRow[d + 2] = (byte)((vLogo * aChroma + dstRow[d + 2] * (255 - aChroma) + 127) / 255);
                        }
                    }
                }
            }
        }

        public static unsafe void ApplyLogoOverlayUyvy(byte* uyvyBuffer)
        {
            if (uyvyBuffer == null) return;

            if (IsNdiOverlayEnabled)
            {
                var ndiOv = CurrentNdiOverlay;
                if (ndiOv != null)
                {
                    BlendPreparedRegionUyvy(uyvyBuffer, ndiOv.StartX, ndiOv.StartY, ndiOv.Width, ndiOv.Height, ndiOv.Y, ndiOv.U, ndiOv.V, ndiOv.A, NdiOverlayOpacityPercent);
                }
            }

            if (IsHtmlOverlayEnabled)
            {
                var htmlOv = CurrentHtmlOverlay;
                if (htmlOv != null)
                {
                    BlendPreparedRegionUyvy(uyvyBuffer, htmlOv.StartX, htmlOv.StartY, htmlOv.Width, htmlOv.Height, htmlOv.Y, htmlOv.U, htmlOv.V, htmlOv.A, HtmlOverlayOpacityPercent);
                }
            }

            if (IsLogoOverlayEnabled)
            {
                var ov = CurrentLogoOverlay;
                if (ov != null && ov.Width > 0 && ov.Height > 0)
                {
                    int startX = (int)Math.Round((1920 - ov.Width) * (Math.Clamp(LogoXPercent, 0.0, 100.0) / 100.0));
                    int startY = (int)Math.Round((1080 - ov.Height) * (Math.Clamp(LogoYPercent, 0.0, 100.0) / 100.0));
                    BlendPreparedRegionUyvy(uyvyBuffer, startX, startY, ov.Width, ov.Height, ov.Y, ov.U, ov.V, ov.A, LogoOpacityPercent);
                }
            }
        }

        public static unsafe void ApplyLogoOverlayUyvy(byte[]? uyvyBytes)
        {
            if ((!IsLogoOverlayEnabled && !IsHtmlOverlayEnabled && !IsNdiOverlayEnabled) || uyvyBytes == null || uyvyBytes.Length < 1920 * 1080 * 2) return;
            fixed (byte* ptr = uyvyBytes)
            {
                ApplyLogoOverlayUyvy(ptr);
            }
        }

        private void BtnBrowseHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select HTML CG Overlay File",
                Filter = "HTML Files (*.html;*.htm)|*.html;*.htm|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtHtmlOverlayUrl.Text = dlg.FileName;
                SaveCurrentSettings();
            }
        }

        private void TxtHtmlOverlayUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                BtnConnectHtmlOverlay_Click(sender, e);
            }
        }

        private async void BtnConnectHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtHtmlOverlayUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                BtnBrowseHtmlOverlay_Click(sender, e);
                url = TxtHtmlOverlayUrl.Text.Trim();
                if (string.IsNullOrEmpty(url)) return;
            }
            SaveCurrentSettings();
            await _htmlOverlaySource.StartAsync(url);
            SaveCurrentSettings();
        }

        private void BtnReloadHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            _htmlOverlaySource?.Reload();
        }

        private void BtnDisconnectHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            _htmlOverlaySource?.Stop();
            SetHtmlOverlayActiveState(false);
            SaveCurrentSettings();
        }

        private async void BtnToggleHtmlOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (!IsHtmlOverlayEnabled && (_htmlOverlaySource == null || !_htmlOverlaySource.IsRunning))
            {
                string url = TxtHtmlOverlayUrl.Text.Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    await _htmlOverlaySource!.StartAsync(url);
                }
                else
                {
                    BtnConnectHtmlOverlay_Click(sender, e);
                    return;
                }
            }
            SetHtmlOverlayActiveState(!IsHtmlOverlayEnabled);
            SaveCurrentSettings();
        }

        private void SetHtmlOverlayActiveState(bool enabled)
        {
            IsHtmlOverlayEnabled = enabled && _htmlOverlaySource != null && _htmlOverlaySource.IsRunning;
            _isUpdatingLogoUi = true;
            try
            {
                if (BtnToggleHtmlOverlay != null)
                {
                    BtnToggleHtmlOverlay.Content = IsHtmlOverlayEnabled ? "CG: ON" : "CG: OFF";
                    BtnToggleHtmlOverlay.Background = IsHtmlOverlayEnabled
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x7d, 0x32))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x42, 0x42));
                }
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }
            UpdateHtmlOverlayPreviewElement();
        }

        private void UpdateHtmlOverlayPreviewElement()
        {
            if (PreviewHtmlOverlay == null) return;
            if (!IsHtmlOverlayEnabled)
            {
                PreviewHtmlOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            PreviewHtmlOverlay.Opacity = Math.Clamp(HtmlOverlayOpacityPercent / 100.0, 0.0, 1.0);
            PreviewHtmlOverlay.Visibility = Visibility.Visible;
        }

        private void SldHtmlOverlayOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            HtmlOverlayOpacityPercent = e.NewValue;
            if (TxtHtmlOverlayOpacityVal != null) TxtHtmlOverlayOpacityVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            UpdateHtmlOverlayPreviewElement();
            SaveCurrentSettings();
        }

        // ---------------- NDI CG Overlay Controls ----------------
        private bool _pendingHtmlOverlayEnabled;
        private string? _pendingNdiOverlaySourceName;

        private void SetupNdiOverlaySource()
        {
            _ndiOverlaySource.OnLog = Log;
            _ndiOverlaySource.OnOverlayFrameUpdated = (frame) => {
                CurrentNdiOverlay = frame;
            };
            _ndiOverlaySource.OnPreviewBufferUpdated = (buf) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    _bmpNdiOverlay.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                    if (TxtNdiOverlayPreviewPlaceholder != null && TxtNdiOverlayPreviewPlaceholder.Visibility != Visibility.Collapsed)
                        TxtNdiOverlayPreviewPlaceholder.Visibility = Visibility.Collapsed;
                });
            };
            _ndiOverlaySource.OnStatusChanged = (status, isLive, isError) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    if (TxtNdiOverlayStatus != null)
                    {
                        TxtNdiOverlayStatus.Text = "Status: " + status;
                        TxtNdiOverlayStatus.Foreground = isError
                            ? System.Windows.Media.Brushes.IndianRed
                            : (isLive ? System.Windows.Media.Brushes.LimeGreen : (System.Windows.Media.Brush)FindResource("TextDim"));
                    }
                    if (!isLive && TxtNdiOverlayPreviewPlaceholder != null)
                        TxtNdiOverlayPreviewPlaceholder.Visibility = Visibility.Visible;

                    bool offline = status == "OFFLINE";
                    BtnConnectNdiOverlay.IsEnabled = offline;
                    BtnDisconnectNdiOverlay.IsEnabled = !offline;
                });
            };
        }

        private async void BtnRefreshNdiOverlay_Click(object sender, RoutedEventArgs e) => await RefreshNdiOverlaySourcesAsync();

        private async Task RefreshNdiOverlaySourcesAsync()
        {
            string? previous = (CmbNdiOverlaySources.SelectedItem as NdiSourceInfo)?.Name ?? _pendingNdiOverlaySourceName;
            BtnRefreshNdiOverlay.IsEnabled = false;
            if (TxtNdiOverlayStatus != null) TxtNdiOverlayStatus.Text = "Status: Searching...";
            try
            {
                var sources = await Task.Run(() => NdiSource.FindSources(2000, Log));
                if (NdiOut.IsRunning)
                    sources.RemoveAll(s => s.Name.Contains(NdiOut.StreamName, StringComparison.OrdinalIgnoreCase));

                CmbNdiOverlaySources.ItemsSource = sources;
                CmbNdiOverlaySources.DisplayMemberPath = "Name";
                var match = sources.FirstOrDefault(s => s.Name == previous);
                CmbNdiOverlaySources.SelectedItem = match ?? (sources.Count > 0 ? sources[0] : null);
                Log($"NDI Overlay: found {sources.Count} source(s)");
                if (!_ndiOverlaySource.IsRunning && TxtNdiOverlayStatus != null)
                    TxtNdiOverlayStatus.Text = sources.Count > 0 ? "Status: OFFLINE" : "Status: No sources";
            }
            catch (Exception ex)
            {
                Log("NDI Overlay refresh error: " + ex.Message);
            }
            finally
            {
                BtnRefreshNdiOverlay.IsEnabled = true;
            }
        }

        private void BtnConnectNdiOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (CmbNdiOverlaySources.SelectedItem is not NdiSourceInfo info)
            {
                if (TxtNdiOverlayStatus != null) TxtNdiOverlayStatus.Text = "Status: Select source";
                return;
            }
            _pendingNdiOverlaySourceName = info.Name;
            BtnConnectNdiOverlay.IsEnabled = false;
            BtnDisconnectNdiOverlay.IsEnabled = true;
            _ndiOverlaySource.Start(info.Name, info.Url);
            SaveCurrentSettings();
        }

        private void BtnDisconnectNdiOverlay_Click(object sender, RoutedEventArgs e)
        {
            _ndiOverlaySource.Stop();
            SetNdiOverlayActiveState(false);
            BtnConnectNdiOverlay.IsEnabled = true;
            BtnDisconnectNdiOverlay.IsEnabled = false;
            SaveCurrentSettings();
        }

        private async void BtnToggleNdiOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (!IsNdiOverlayEnabled && !_ndiOverlaySource.IsRunning)
            {
                if (CmbNdiOverlaySources.SelectedItem is NdiSourceInfo)
                {
                    BtnConnectNdiOverlay_Click(sender, e);
                    return;
                }
                else
                {
                    await RefreshNdiOverlaySourcesAsync();
                    if (CmbNdiOverlaySources.SelectedItem is NdiSourceInfo)
                    {
                        BtnConnectNdiOverlay_Click(sender, e);
                        return;
                    }
                }
            }
            SetNdiOverlayActiveState(!IsNdiOverlayEnabled);
            SaveCurrentSettings();
        }

        private void SetNdiOverlayActiveState(bool enabled)
        {
            IsNdiOverlayEnabled = enabled && _ndiOverlaySource != null && _ndiOverlaySource.IsRunning;
            _isUpdatingLogoUi = true;
            try
            {
                if (BtnToggleNdiOverlay != null)
                {
                    BtnToggleNdiOverlay.Content = IsNdiOverlayEnabled ? "CG: ON" : "CG: OFF";
                    BtnToggleNdiOverlay.Background = IsNdiOverlayEnabled
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2e, 0x7d, 0x32))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x42, 0x42));
                }
            }
            finally
            {
                _isUpdatingLogoUi = false;
            }
            UpdateNdiOverlayPreviewElement();
        }

        private void UpdateNdiOverlayPreviewElement()
        {
            if (PreviewNdiOverlay == null) return;
            if (!IsNdiOverlayEnabled)
            {
                PreviewNdiOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            PreviewNdiOverlay.Opacity = Math.Clamp(NdiOverlayOpacityPercent / 100.0, 0.0, 1.0);
            PreviewNdiOverlay.Visibility = Visibility.Visible;
        }

        private void SldNdiOverlayOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            NdiOverlayOpacityPercent = e.NewValue;
            if (TxtNdiOverlayOpacityVal != null) TxtNdiOverlayOpacityVal.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (_isUpdatingLogoUi || !IsLoaded) return;
            UpdateNdiOverlayPreviewElement();
            SaveCurrentSettings();
        }

        #endregion
        
        private void BtnBrowseRecordingDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                TxtRecordingDirectory.Text = dialog.FolderName;
                SaveCurrentSettings();
            }
        }
        
        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (LclRecorder.IsRecording)
            {
                LclRecorder.Stop();
                BtnRecord.Content = "START RECORDING";
                BtnRecord.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#d32f2f"));
                TxtStatus.Text = "Recording stopped.";
            }
            else
            {
                if (CmbRecordingProfile.SelectedItem is RecordingProfileDefinition profile)
                {
                    string dir = TxtRecordingDirectory.Text;
                    if (string.IsNullOrWhiteSpace(dir)) { System.Windows.MessageBox.Show("Please enter a recording directory."); return; }
                    if (!System.IO.Directory.Exists(dir))
                    {
                        try { System.IO.Directory.CreateDirectory(dir); }
                        catch { System.Windows.MessageBox.Show("Invalid recording directory."); return; }
                    }
                    UpdatePgmPreviewSynthetic();
                    LclRecorder.Start(profile, dir, 1920, 1080, 25);
                    BtnRecord.Content = "STOP RECORDING";
                    BtnRecord.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 170, 0));
                    TxtStatus.Text = "Recording started...";
                }
            }
        }
        
        private void BtnStream_Click(object sender, RoutedEventArgs e)
        {
            if (YtStreamer.IsStreaming)
            {
                
                YtStreamer.Stop();
                BtnStream.Content = "START STREAM";
                BtnStream.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 0, 0));
                TxtStatus.Text = "Streaming stopped.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(TxtStreamKey.Text))
                {
                    System.Windows.MessageBox.Show("Please enter a YouTube Stream Key.");
                    return;
                }
                
                // Save it immediately so it is persistent
                var settings = AppSettings.Load();
                settings.YouTubeStreamKey = TxtStreamKey.Text;
                settings.Save();
                
                BtnStream.Content = "STOP STREAM";
                BtnStream.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 170, 0));
                TxtStatus.Text = "Streaming started...";
                // Seed the latest PGM frame immediately (synchronous) so stream loop has data right away
                UpdatePgmPreviewSynthetic();
                YtStreamer.Start(TxtStreamKey.Text, 1920, 1080, 25);
                
            }
        }

        private void BtnNdi_Click(object sender, RoutedEventArgs e)
        {
            if (NdiOut.IsRunning)
            {
                NdiOut.Stop();
                BtnNdi.Content = "START NDI";
                BtnNdi.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 102, 204));
                TxtStatus.Text = "NDI output stopped.";
            }
            else
            {
                string streamName = TxtNdiName.Text.Trim();
                if (string.IsNullOrWhiteSpace(streamName))
                {
                    streamName = "DeckLink Switcher - Program";
                    TxtNdiName.Text = streamName;
                }

                var settings = AppSettings.Load();
                settings.NdiStreamName = streamName;
                settings.Save();

                UpdatePgmPreviewSynthetic();
                bool started = NdiOut.Start(streamName, 1920, 1080, 25);
                if (started)
                {
                    BtnNdi.Content = "STOP NDI";
                    BtnNdi.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 170, 0));
                    TxtStatus.Text = $"NDI output started: {streamName}";
                }
                else
                {
                    System.Windows.MessageBox.Show("Failed to initialize NDI output. Ensure Processing.NDI.Lib.x64.dll is present.", "NDI Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private bool _isUpdatingSrtUrl = false;

        private string BuildSrtUrl()
        {
            string mode = (CmbSrtMode?.SelectedItem as ComboBoxItem)?.Content?.ToString()?.ToLowerInvariant() ?? "caller";
            string host = TxtSrtHost?.Text?.Trim() ?? "127.0.0.1";
            if (string.IsNullOrWhiteSpace(host))
            {
                host = mode == "listener" ? "0.0.0.0" : "127.0.0.1";
            }
            else if (mode == "listener" && (host == "127.0.0.1" || host == "localhost"))
            {
                host = "0.0.0.0";
            }

            if (!int.TryParse(TxtSrtPort?.Text?.Trim(), out int port) || port <= 0)
            {
                port = 9000;
            }

            if (!int.TryParse(TxtSrtLatency?.Text?.Trim(), out int latencyMs) || latencyMs < 0)
            {
                latencyMs = 200;
            }
            long latencyUs = (long)latencyMs * 1000;

            var queryParams = new List<string>
            {
                $"mode={mode}",
                $"latency={latencyUs}"
            };

            string streamId = TxtSrtStreamId?.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(streamId))
            {
                queryParams.Add($"streamid={Uri.EscapeDataString(streamId)}");
            }

            string passphrase = TxtSrtPassphrase?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(passphrase))
            {
                queryParams.Add($"passphrase={Uri.EscapeDataString(passphrase)}");
                queryParams.Add("pbkeylen=16");
            }

            return $"srt://{host}:{port}?{string.Join("&", queryParams)}";
        }

        private void UpdateGeneratedSrtUrl()
        {
            if (_isUpdatingSrtUrl || TxtSrtUrl == null || CmbSrtMode == null || TxtSrtHost == null || TxtSrtPort == null || TxtSrtLatency == null) return;
            _isUpdatingSrtUrl = true;
            try
            {
                TxtSrtUrl.Text = BuildSrtUrl();
            }
            finally
            {
                _isUpdatingSrtUrl = false;
            }
        }

        private void CmbSrtMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSrtMode == null || TxtSrtHost == null || LblSrtHost == null) return;
            string mode = (CmbSrtMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Caller";
            if (mode == "Listener")
            {
                LblSrtHost.Text = "Listen IP (0.0.0.0 for any interface):";
                if (TxtSrtHost.Text == "127.0.0.1" || string.IsNullOrWhiteSpace(TxtSrtHost.Text))
                {
                    TxtSrtHost.Text = "0.0.0.0";
                }
            }
            else
            {
                LblSrtHost.Text = "Host / IP (Caller Mode):";
                if (TxtSrtHost.Text == "0.0.0.0")
                {
                    TxtSrtHost.Text = "127.0.0.1";
                }
            }
            UpdateGeneratedSrtUrl();
        }

        private void SrtInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateGeneratedSrtUrl();
        }

        private void BtnSrt_Click(object sender, RoutedEventArgs e)
        {
            if (SrtOut.IsStreaming)
            {
                SrtOut.Stop();
                UpdateSrtUiState(false);
                TxtStatus.Text = "SRT streaming stopped.";
            }
            else
            {
                string srtUrl = TxtSrtUrl?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(srtUrl))
                {
                    srtUrl = BuildSrtUrl();
                    if (TxtSrtUrl != null) TxtSrtUrl.Text = srtUrl;
                }

                if (!srtUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Please enter a valid SRT URL starting with 'srt://'.", "Invalid SRT URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SaveCurrentSettings();

                UpdatePgmPreviewSynthetic();
                SrtOut.Start(srtUrl, 1920, 1080, 25);
                if (SrtOut.IsStreaming)
                {
                    UpdateSrtUiState(true);
                    TxtStatus.Text = $"SRT streaming started: {srtUrl}";
                }
                else
                {
                    UpdateSrtUiState(false);
                    TxtStatus.Text = "Failed to start SRT output. Check logs.";
                }
            }
        }

        private void UpdateSrtUiState(bool isStreaming)
        {
            if (BtnSrt == null) return;
            if (isStreaming)
            {
                BtnSrt.Content = "STOP SRT";
                BtnSrt.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 170, 0));
            }
            else
            {
                BtnSrt.Content = "START SRT";
                BtnSrt.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 102, 204));
            }
        }

        public static void Log(string message)
        {
            try
            {
                string logLine = $"{DateTime.Now:HH:mm:ss.fff} - {message}";
                File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
            }
            catch { }
        }

        private void BtnApplySettings_Click(object sender, RoutedEventArgs e)
        {
            BtnApplySettings.IsEnabled = false;
            var outInfo = CmbOutput.SelectedItem as DeckLinkDeviceInfo;


            if (outInfo == null)
            {
                TxtStatus.Text = "Please select an Output option (or None).";
                BtnApplySettings.IsEnabled = true;
                return;
            }

            // Stop synthetic timer & existing devices if any
            _syntheticCts?.Cancel();
            if (_activeOutput != null) _activeOutput.StopPlayback();
            foreach (var input in DynamicInputs)
            {
                if (input.Device != null) input.Device.StopCapture();
            }
            
            _activeOutput = null;
            DynamicInputs.Clear();
            
            for (int i = SwitcherPanel.Children.Count - 1; i >= 0; i--)
            {
                if ((SwitcherPanel.Children[i] as FrameworkElement)?.DataContext is DeckLinkInputViewModel)
                {
                    SwitcherPanel.Children.RemoveAt(i);
                }
            }
            
            _bmpOutput = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            PreviewOutput.Source = _bmpOutput;

            SwitcherPanel.IsEnabled = false;
            CmbOutput.IsEnabled = false;
            InputSelectorsControl.IsEnabled = false;
            BtnApplySettings.IsEnabled = false;
            TxtStatus.Text = "Initializing...";
            
            var selectedInputs = new System.Collections.Generic.List<SelectedInputConfig>();
            foreach (var selector in InputSelectors)
            {
                selectedInputs.Add(new SelectedInputConfig { Device = selector.SelectedDevice, Format = selector.SelectedFormat });
            }

            Thread mtaThread = new Thread(() =>
            {
                try
                {
                    InitializeDynamicRouting(outInfo, selectedInputs, _bmpOutput);
                }
                catch (Exception ex)
                {
                    Log($"MTA Init Error: {ex.Message}");
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BtnApplySettings.IsEnabled = true;
                        TxtStatus.Text = "Error: " + ex.Message;
                    }));
                }
            });
            mtaThread.SetApartmentState(ApartmentState.MTA);
            mtaThread.IsBackground = true;
            mtaThread.Start();
            
            SaveCurrentSettings();
        }

        private int _activeSourceType = 0; // 0=Device, 1=ColorBars, 2=Matte, 3=Media
        public static int ActiveSourceType = 0;
        public static string ActiveInputName = "";

        private void ClearPgmPreview()
        {
            if (_bmpOutput == null) return;
            byte[] black = new byte[480 * 270 * 4];
            for (int i = 0; i < black.Length; i += 4)
            {
                black[i] = 0;
                black[i + 1] = 0;
                black[i + 2] = 0;
                black[i + 3] = 255;
            }
            _bmpOutput.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), black, 480 * 4, 0);
            AudioOutputL.Value = 0;
            AudioOutputR.Value = 0;
        }



        private void UpdateProgramSourceFlags()
        {
            var defaultBrush = (System.Windows.Media.Brush)FindResource("SourceButtonBg");
            var defaultTextBrush = (System.Windows.Media.Brush)FindResource("SourceButtonText");
            string defaultBgHex = (defaultBrush as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "#333344";
            string defaultFgHex = (defaultTextBrush as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "#ffffff";

            foreach (var input in DynamicInputs)
            {
                bool isActive = _activeSourceType == 0 && input.Device == _activeInput && _activeInput != null;
                if (input.Device != null) input.Device.IsProgramSource = isActive;
                input.BgColor = isActive ? "#d32f2f" : defaultBgHex;
                input.FgColor = isActive ? "#ffffff" : defaultFgHex;
            }

            foreach (var scr in DynamicScreens)
            {
                bool isActive = _activeSourceType == 4 && _activeScreen == scr;
                scr.BgColor = isActive ? "#d32f2f" : defaultBgHex;
                scr.FgColor = isActive ? "#ffffff" : defaultFgHex;
            }
            foreach (var cam in DynamicWebcams)
            {
                bool isActive = _activeSourceType == 5 && _activeWebcam == cam;
                cam.BgColor = isActive ? "#d32f2f" : defaultBgHex;
                cam.FgColor = isActive ? "#ffffff" : defaultFgHex;
            }

            var activeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd3, 0x2f, 0x2f));
            var activeTextBrush = System.Windows.Media.Brushes.White;

            Action<System.Windows.Controls.Button?, bool> applySourceButtonStyle = (btn, isActive) =>
            {
                if (btn == null) return;
                btn.Background = isActive ? activeBrush : defaultBrush;
                btn.Foreground = isActive ? activeTextBrush : defaultTextBrush;
            };

            applySourceButtonStyle(BtnColorBars, _activeSourceType == 1);
            applySourceButtonStyle(BtnMatte, _activeSourceType == 2);
            applySourceButtonStyle(BtnMedia, _activeSourceType == 3);
            applySourceButtonStyle(BtnYouTube, _activeSourceType == 6);
            applySourceButtonStyle(BtnNdiSource, _activeSourceType == 8);
            applySourceButtonStyle(BtnHtmlSource, _activeSourceType == 7);

            bool isSysAudioActive = (StateSystemAudio == AudioState.ON) || (StateSystemAudio == AudioState.AFV && _activeSourceType == 4);
            applySourceButtonStyle(BtnSystemAudio, isSysAudioActive);

            UpdateHtmlSourceMuteState();
        }

        private void UpdateHtmlSourceMuteState()
        {
            if (_htmlSource == null) return;
            bool shouldPlayAudio = (StateHtml == AudioState.ON) || (StateHtml == AudioState.AFV && _activeSourceType == 7);
            _htmlSource.SetMuted(!shouldPlayAudio);
        }

        private void BtnColorBars_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 1; ActiveSourceType = 1; ActiveInputName = "Color Bars";
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
            Log("Switched to Color Bars");
            UpdatePgmPreviewSynthetic();
            double val = 50 * ColorBarsAudioLevel * PgmAudioLevel;
            AudioOutputL.Value = val;
            AudioOutputR.Value = val;
        }
        private void PreviewColorBars_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnColorBars_Click(null, null);

        private void BtnMatte_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 2; ActiveSourceType = 2; ActiveInputName = "Matte";
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
            Log("Switched to Matte");
            UpdatePgmPreviewSynthetic();
            AudioOutputL.Value = 0;
            AudioOutputR.Value = 0;
        }
        private void PreviewMatte_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnMatte_Click(null, null);

        private void BtnMedia_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 3; ActiveSourceType = 3; ActiveInputName = "Media"; Log("Switched to Local Video");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
        }
        private void PreviewMedia_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnMedia_Click(null, null);

        private void BtnYouTube_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 6; ActiveSourceType = 6; ActiveInputName = "YouTube"; Log("Switched to YouTube Live");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();

            if (_youtubeSource != null)
            {
                var preview = _youtubeSource.GetLatestPreviewPixels();
                if (preview != null && _bmpOutput != null)
                {
                    _bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), preview, 480 * 4, 0);
                }
                var pgm = _youtubeSource.GetLatestPgmPixels();
                if (pgm != null)
                {
                    lock (_pgmLock)
                    {
                        if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                            _latestPgmFrame = new byte[1920 * 1080 * 4];
                        Array.Copy(pgm, _latestPgmFrame, _latestPgmFrame.Length);
                    }
                }
            }
        }
        private void PreviewYouTube_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnYouTube_Click(null, null);

        // ---------------- NDI Source ----------------
        private void SetupNdiSource()
        {
            _ndiSource.OnLog = Log;
            _ndiSource.OnPreviewBufferUpdated = (buf) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    _bmpNdi.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                    if (_activeSourceType == 8)
                    {
                        _bmpOutput?.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                    }
                });
            };
            _ndiSource.OnAudioLevelArrived = (l, r) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    double nL = l * _ndiSource.AudioLevel;
                    double nR = r * _ndiSource.AudioLevel;
                    AudioBarNdiL.Value = nL; AudioBarNdiR.Value = nR;
                    if (_activeSourceType == 8)
                    {
                        AudioOutputL.Value = nL * PgmAudioLevel;
                        AudioOutputR.Value = nR * PgmAudioLevel;
                    }
                });
            };
            _ndiSource.OnStatusChanged = (status, isLive, isError) => {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    TxtNdiStatus.Text = status;
                    if (isError)
                    {
                        TxtNdiStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                        TxtNdiStatusIcon.Foreground = System.Windows.Media.Brushes.IndianRed;
                        PnlNdiStatusOverlay.Visibility = Visibility.Visible;
                        BtnConnectNdi.IsEnabled = true;
                        BtnDisconnectNdi.IsEnabled = false;
                    }
                    else if (isLive)
                    {
                        TxtNdiStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                        TxtNdiStatusIcon.Foreground = System.Windows.Media.Brushes.LimeGreen;
                        PnlNdiStatusOverlay.Visibility = Visibility.Collapsed;
                        BtnConnectNdi.IsEnabled = false;
                        BtnDisconnectNdi.IsEnabled = true;
                    }
                    else
                    {
                        TxtNdiStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
                        TxtNdiStatusIcon.Foreground = System.Windows.Media.Brushes.MediumOrchid;
                        PnlNdiStatusOverlay.Visibility = Visibility.Visible;
                        // "Connecting..." / "No signal" keep Stop available; OFFLINE re-enables Connect
                        bool offline = status == "OFFLINE";
                        BtnConnectNdi.IsEnabled = offline;
                        BtnDisconnectNdi.IsEnabled = !offline;
                    }
                });
            };
        }

        private void BtnNdiSource_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 8; ActiveSourceType = 8; ActiveInputName = "NDI"; Log("Switched to NDI Source");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
            _ = AutoConnectNdiAsync();
        }

        // Scans for sources if needed and connects to the selected / last used one.
        private async Task AutoConnectNdiAsync()
        {
            try
            {
                if (_ndiSource.IsStreaming) return;
                if (CmbNdiSources.Items.Count == 0) await RefreshNdiSourcesAsync();
                if (!_ndiSource.IsStreaming && CmbNdiSources.SelectedItem is NdiSourceInfo)
                    BtnConnectNdi_Click(null, null);
            }
            catch (Exception ex) { Log("NDI auto-connect error: " + ex.Message); }
        }
        private void PreviewNdi_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnNdiSource_Click(null, null);

        private async void BtnRefreshNdi_Click(object sender, RoutedEventArgs e) => await RefreshNdiSourcesAsync();

        private async Task RefreshNdiSourcesAsync()
        {
            string? previous = (CmbNdiSources.SelectedItem as NdiSourceInfo)?.Name ?? _pendingNdiSourceName;
            BtnRefreshNdi.IsEnabled = false;
            TxtNdiStatus.Text = "Searching...";
            try
            {
                var sources = await Task.Run(() => NdiSource.FindSources(2000, Log));
                // Don't list our own NDI program output (would create a feedback loop)
                if (NdiOut.IsRunning)
                    sources.RemoveAll(s => s.Name.Contains(NdiOut.StreamName, StringComparison.OrdinalIgnoreCase));

                CmbNdiSources.ItemsSource = sources;
                CmbNdiSources.DisplayMemberPath = "Name";
                var match = sources.FirstOrDefault(s => s.Name == previous);
                CmbNdiSources.SelectedItem = match ?? (sources.Count > 0 ? sources[0] : null);
                Log($"NDI: found {sources.Count} source(s)");
                if (!_ndiSource.IsStreaming) TxtNdiStatus.Text = sources.Count > 0 ? "OFFLINE" : "No sources";
            }
            catch (Exception ex)
            {
                Log("NDI refresh error: " + ex.Message);
            }
            finally
            {
                BtnRefreshNdi.IsEnabled = true;
            }
        }

        private string? _pendingNdiSourceName;

        private void BtnConnectNdi_Click(object sender, RoutedEventArgs e)
        {
            if (CmbNdiSources.SelectedItem is not NdiSourceInfo info)
            {
                TxtNdiStatus.Text = "Select a source";
                return;
            }
            _pendingNdiSourceName = info.Name;
            BtnConnectNdi.IsEnabled = false;
            BtnDisconnectNdi.IsEnabled = true;
            _ndiSource.Start(info);
            SaveCurrentSettings();
        }

        private void BtnDisconnectNdi_Click(object sender, RoutedEventArgs e)
        {
            _ndiSource.Stop();
            BtnConnectNdi.IsEnabled = true;
            BtnDisconnectNdi.IsEnabled = false;
        }

        private void SldNdiAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_ndiSource != null) _ndiSource.AudioLevel = (float)e.NewValue;
            NdiAudioLevel = (float)e.NewValue;
        }

        private async void BtnConnectYouTube_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtYouTubeUrl.Text.Trim();
            SaveCurrentSettings();
            BtnConnectYouTube.IsEnabled = false;
            BtnDisconnectYouTube.IsEnabled = true;
            await _youtubeSource.StartAsync(url);
        }

        private void BtnDisconnectYouTube_Click(object sender, RoutedEventArgs e)
        {
            _youtubeSource.Stop();
            BtnConnectYouTube.IsEnabled = true;
            BtnDisconnectYouTube.IsEnabled = false;
        }

        private void SldYouTubeAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_youtubeSource != null) _youtubeSource.AudioLevel = (float)e.NewValue;
            YouTubeAudioLevel = (float)e.NewValue;
        }

        private static string FormatDelayText(int ms) => ms >= 1000 ? $"{ms / 1000.0:0.0}s" : $"{ms}ms";

        private void SldYouTubeAudioDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int ms = (int)e.NewValue;
            if (TxtYouTubeAudioDelay != null) TxtYouTubeAudioDelay.Text = FormatDelayText(ms);
            if (_youtubeSource != null) _youtubeSource.AudioDelayMs = ms;
            SaveCurrentSettings();
        }

        private void BtnHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 7; ActiveSourceType = 7; ActiveInputName = "HTML"; Log("Switched to HTML / Web Source");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();

            if (_htmlSource != null)
            {
                var preview = _htmlSource.GetLatestPreviewPixels();
                if (preview != null && _bmpOutput != null)
                {
                    _bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), preview, 480 * 4, 0);
                }
                var pgm = _htmlSource.GetLatestPgmPixels();
                if (pgm != null)
                {
                    lock (_pgmLock)
                    {
                        if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                            _latestPgmFrame = new byte[1920 * 1080 * 4];
                        Array.Copy(pgm, _latestPgmFrame, _latestPgmFrame.Length);
                    }
                }
            }
        }
        private void PreviewHtmlSource_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnHtmlSource_Click(null, null);

        private void BtnBrowseHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Local HTML File",
                Filter = "HTML Files (*.html;*.htm)|*.html;*.htm|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtHtmlSourceUrl.Text = dlg.FileName;
                SaveCurrentSettings();
            }
        }

        private void TxtHtmlSourceUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                BtnConnectHtmlSource_Click(sender, e);
            }
        }

        private async void BtnConnectHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtHtmlSourceUrl.Text.Trim();
            SaveCurrentSettings();
            BtnDisconnectHtmlSource.IsEnabled = true;
            await _htmlSource.StartAsync(url);
            UpdateHtmlSourceMuteState();
        }

        private void BtnReloadHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            _htmlSource?.Reload();
        }

        private void BtnDisconnectHtmlSource_Click(object sender, RoutedEventArgs e)
        {
            _htmlSource?.Stop();
            BtnConnectHtmlSource.IsEnabled = true;
            BtnDisconnectHtmlSource.IsEnabled = false;
        }

        public void SwitchToScreen(ScreenInputViewModel vm)
        {
            _activeSourceType = 4;
            ActiveSourceType = 4;
            _activeScreen = vm;
            _activeWebcam = null;
            _activeInput = null;
            ActiveInputName = vm.DisplayName;
            Log($"Switched to {vm.DisplayName}");
            UpdateProgramSourceFlags();

            if (vm.Context != null && vm.Context.Prev480Buffer != null && _bmpOutput != null)
            {
                _bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), vm.Context.Prev480Buffer, 480 * 4, 0);
            }
            if (vm.Context != null && vm.Context.Pgm1080Buffer != null)
            {
                lock (_pgmLock)
                {
                    if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                        _latestPgmFrame = new byte[1920 * 1080 * 4];
                    Array.Copy(vm.Context.Pgm1080Buffer, _latestPgmFrame, _latestPgmFrame.Length);
                }
            }
        }

        public void SwitchToWebcam(WebcamInputViewModel vm)
        {
            _activeSourceType = 5;
            ActiveSourceType = 5;
            _activeWebcam = vm;
            _activeScreen = null;
            _activeInput = null;
            ActiveInputName = vm.DisplayName;
            Log($"Switched to {vm.DisplayName}");
            UpdateProgramSourceFlags();

            if (vm.Source != null)
            {
                var preview = vm.Source.GetLatestPreviewPixels();
                if (preview != null && _bmpOutput != null)
                {
                    _bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), preview, 480 * 4, 0);
                }
                var pgm = vm.Source.GetLatestPgmPixels();
                if (pgm != null)
                {
                    lock (_pgmLock)
                    {
                        if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                            _latestPgmFrame = new byte[1920 * 1080 * 4];
                        Array.Copy(pgm, _latestPgmFrame, _latestPgmFrame.Length);
                    }
                }
            }
        }

        private string? FindWebcamAudioDevice(string webcamName, System.Collections.Generic.List<MicDeviceInfo>? availableMics = null)
        {
            try
            {
                var mics = availableMics ?? LocalAudioInput.GetDevices();
                MicDeviceInfo? bestMatch = mics.FirstOrDefault(d => 
                    d.DeviceNumber >= 0 && 
                    !string.IsNullOrEmpty(d.Name) && 
                    (d.Name.IndexOf(webcamName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     webcamName.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0));

                if (bestMatch == null)
                {
                    string[] tokens = webcamName.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var token in tokens)
                    {
                        if (token.Length >= 3 && !token.Equals("camera", StringComparison.OrdinalIgnoreCase))
                        {
                            bestMatch = mics.FirstOrDefault(d => d.DeviceNumber >= 0 && d.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (bestMatch != null) break;
                        }
                    }
                }

                if (bestMatch == null)
                {
                    bestMatch = mics.FirstOrDefault(d => d.DeviceNumber >= 0 && d.Name.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (bestMatch != null)
                {
                    Log($"[Webcam] Linked audio device {bestMatch.Name} to {webcamName}");
                    return bestMatch.DeviceId;
                }
                else
                {
                    Log($"[Webcam] No matching microphone found for {webcamName}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log($"[Webcam] Audio link error: {ex.Message}");
                return null;
            }
        }

        private string _lastMediaFile = "";

        private void BtnSelectMedia_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Video and Image Files|*.mp4;*.mkv;*.avi;*.mov;*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                if (_mediaSource != null)
                {
                    _lastMediaFile = dlg.FileName;
                    _mediaSource.Play(dlg.FileName, ChkLoopMedia.IsChecked == true);
                }
                else
                {
                    System.Windows.MessageBox.Show("Media source is not initialized. Please check logs.");
                }
            }
        }

        private void BtnCueMedia_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaSource != null && !string.IsNullOrEmpty(_lastMediaFile))
            {
                _mediaSource.Cue(_lastMediaFile, ChkLoopMedia.IsChecked == true);
            }
            else
            {
                System.Windows.MessageBox.Show("No media file selected. Please select a file first.");
            }
        }

        private void BtnPauseMedia_Click(object sender, RoutedEventArgs e)
        {
            _mediaSource?.Pause();
        }

        private void BtnResumeMedia_Click(object sender, RoutedEventArgs e)
        {
            _mediaSource?.Resume();
        }

        private void BtnStopMedia_Click(object sender, RoutedEventArgs e)
        {
            _mediaSource?.Stop();
        }

        private void SeekTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_cpuCounter != null)
                {
                    float cpu = _cpuCounter.NextValue();
                    int cpuInt = (int)Math.Round(cpu);
                    TxtCpuUsage.Text = $"{cpuInt}%";

                    // Color grading: Good (Green < 60%), Warning (Yellow 60% - 84%), Danger (Red >= 85%)
                    if (cpuInt < 60)
                    {
                        TxtCpuUsage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xe6, 0x76)); // Vibrant Green
                    }
                    else if (cpuInt < 85)
                    {
                        TxtCpuUsage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0xd6, 0x00)); // Vivid Yellow/Amber
                    }
                    else
                    {
                        TxtCpuUsage.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0x17, 0x44)); // Bright Red
                    }
                }
            }
            catch { }
            
            if (_mediaSource != null && !_isDraggingSeek)
            {
                float pos = _mediaSource.Position;
                if (pos >= 0 && pos <= 1)
                {
                    SldMediaSeek.Value = pos;
                }
            }
        }

        private void SldMediaSeek_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isDraggingSeek = true;
        }

        private void SldMediaSeek_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _isDraggingSeek = false;
            if (_mediaSource != null)
            {
                _mediaSource.Position = (float)SldMediaSeek.Value;
            }
        }

        private void SldMediaSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingSeek && _mediaSource != null)
            {
                _mediaSource.Position = (float)e.NewValue;
            }
        }

        public static byte MatteY = 41;
        public static byte MatteU = 212;
        public static byte MatteV = 114;

        private void ChkSystemAudioOut_Checked(object sender, RoutedEventArgs e)
        {
            SystemAudioPlayer.IsEnabled = true;
            if (IsLoaded) SaveCurrentSettings();
        }

        private void ChkSystemAudioOut_Unchecked(object sender, RoutedEventArgs e)
        {
            SystemAudioPlayer.IsEnabled = false;
            if (IsLoaded) SaveCurrentSettings();
        }
        
        public static volatile bool InvertDeckLinkRight = false;

        private void ChkInvertDeckLinkR_Changed(object sender, RoutedEventArgs e)
        {
            InvertDeckLinkRight = ChkInvertDeckLinkR.IsChecked == true;
            if (IsLoaded) SaveCurrentSettings();
        }

        private void ChkDarkMode_Checked(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app) app.SetTheme(true);
            UpdateProgramSourceFlags();
        }

        private void ChkDarkMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app) app.SetTheme(false);
            UpdateProgramSourceFlags();
        }

        private static float _pgmAudioLevel = 1.0f;
        public static float PgmAudioLevel { get { return _pgmAudioLevel; } set { _pgmAudioLevel = value; } }

        public static float ColorBarsAudioLevel = 1.0f;
        public static float MediaAudioLevel = 1.0f;
        public static float WebcamAudioLevel = 1.0f;

        public static AudioState StateMedia = AudioState.AFV;
        public static AudioState StateColorBars = AudioState.AFV;
        public static AudioState StateWebcam = AudioState.AFV;
        
        public static System.Collections.Concurrent.ConcurrentDictionary<string, float> DynamicMicLevels = new();
        public static System.Collections.Concurrent.ConcurrentDictionary<string, AudioState> DynamicMicStates = new();
        
        private sealed class AudioFifo
        {
            private const int MaximumBufferedSamples = 19200;
            private readonly Queue<short[]> _packets = new();
            private readonly object _lock = new();
            private int _headOffset;
            private int _count;
            private long _capturedSamples;
            private long _mixedSamples;
            private long _trimmedSamples;
            private long _underrunSamples;

            public void Enqueue(short[] packet)
            {
                lock (_lock)
                {
                    _packets.Enqueue(packet);
                    _count += packet.Length;
                    _capturedSamples += packet.Length;
                    TrimTo(MaximumBufferedSamples);
                }
            }

            public void MixInto(int[] destination, int sampleCount, float level)
            {
                lock (_lock)
                {
                    int destinationIndex = 0;
                    while (destinationIndex < sampleCount && _packets.Count > 0)
                    {
                        short[] packet = _packets.Peek();
                        int packetSamples = Math.Min(sampleCount - destinationIndex, packet.Length - _headOffset);
                        for (int i = 0; i < packetSamples; i++)
                        {
                            destination[destinationIndex + i] += (int)(packet[_headOffset + i] * level);
                        }

                        destinationIndex += packetSamples;
                        _headOffset += packetSamples;
                        _count -= packetSamples;
                        if (_headOffset == packet.Length)
                        {
                            _packets.Dequeue();
                            _headOffset = 0;
                        }
                    }

                    _mixedSamples += destinationIndex;
                    _underrunSamples += sampleCount - destinationIndex;
                }
            }

            public void Drain(int sampleCount)
            {
                lock (_lock)
                {
                    int drained = 0;
                    while (drained < sampleCount && _packets.Count > 0)
                    {
                        short[] packet = _packets.Peek();
                        int packetSamples = Math.Min(sampleCount - drained, packet.Length - _headOffset);
                        drained += packetSamples;
                        _headOffset += packetSamples;
                        _count -= packetSamples;
                        if (_headOffset == packet.Length)
                        {
                            _packets.Dequeue();
                            _headOffset = 0;
                        }
                    }
                }
            }

            public string GetDiagnostics()
            {
                lock (_lock)
                {
                    double pendingMilliseconds = _count * 1000.0 / 96000.0;
                    return $"pendingMs={pendingMilliseconds:F0}, captured={_capturedSamples}, mixed={_mixedSamples}, trimmed={_trimmedSamples}, underrun={_underrunSamples}";
                }
            }

            private void TrimTo(int maximumCount)
            {
                while (_count > maximumCount && _packets.Count > 0)
                {
                    short[] packet = _packets.Peek();
                    int discard = Math.Min(_count - maximumCount, packet.Length - _headOffset);
                    _headOffset += discard;
                    _count -= discard;
                    _trimmedSamples += discard;
                    if (_headOffset == packet.Length)
                    {
                        _packets.Dequeue();
                        _headOffset = 0;
                    }
                }
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AudioFifo> AudioFifos = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> MixerSampleDemand = new();
        
        public static void PushAudioPacket(string name, short[] packet)
        {
            var q = AudioFifos.GetOrAdd(name, _ => new AudioFifo());
            q.Enqueue(packet);
        }

        private void SldPgmAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PgmAudioLevel = (float)e.NewValue;
        }

        private void SldColorBarsAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ColorBarsAudioLevel = (float)e.NewValue;
            double val = 50 * ColorBarsAudioLevel; // 50% is 16384 out of 32768
            AudioBarCBL.Value = val;
            AudioBarCBR.Value = val;
            if (_activeSourceType == 1)
            {
                AudioOutputL.Value = val;
                AudioOutputR.Value = val;
            }
        }
        
        private void SldMediaAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_mediaSource != null) _mediaSource.AudioLevel = (float)e.NewValue; MediaAudioLevel = (float)e.NewValue; }

        private void CmbAudioState_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            StateMedia = (AudioState)CmbAudioStateMedia.SelectedIndex;
            StateColorBars = (AudioState)CmbAudioStateColorBars.SelectedIndex;
            if (CmbAudioStateYouTube != null && CmbAudioStateYouTube.SelectedIndex >= 0)
                StateYouTube = (AudioState)CmbAudioStateYouTube.SelectedIndex;
            if (CmbAudioStateNdi != null && CmbAudioStateNdi.SelectedIndex >= 0)
                StateNdi = (AudioState)CmbAudioStateNdi.SelectedIndex;
            if (CmbAudioStateHtml != null && CmbAudioStateHtml.SelectedIndex >= 0)
                StateHtml = (AudioState)CmbAudioStateHtml.SelectedIndex;
            UpdateHtmlSourceMuteState();
            SaveCurrentSettings();
        }

        private void SldSystemAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SystemAudioLevel = (float)e.NewValue;
            SaveCurrentSettings();
        }

        private void CmbAudioStateSystemAudio_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            StateSystemAudio = (AudioState)CmbAudioStateSystemAudio.SelectedIndex;
            UpdateProgramSourceFlags();
            SaveCurrentSettings();
        }

        private void BtnSystemAudio_Click(object sender, RoutedEventArgs e)
        {
            if (CmbAudioStateSystemAudio.SelectedIndex == 1)
                CmbAudioStateSystemAudio.SelectedIndex = 2; // ON -> OFF
            else
                CmbAudioStateSystemAudio.SelectedIndex = 1; // OFF/AFV -> ON
        }

        private void CmbSystemAudioDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            string? devId = (CmbSystemAudioDevice.SelectedItem as RenderDeviceInfo)?.DeviceId;
            _systemAudioSource.Start(devId);
            SaveCurrentSettings();
        }

        private System.Windows.Controls.StackPanel CreateMicPanel(MicDeviceInfo device, string key, double initialLevel, AudioState initialState)
        {
            var pnl = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            var horiz = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new System.Windows.Thickness(0, 0, 0, 10) };
            
            var barL = new System.Windows.Controls.ProgressBar { Name = "BarL_" + device.DeviceNumber, Orientation = System.Windows.Controls.Orientation.Vertical, Minimum = 0, Maximum = 100, Width = 10, Height = 100, Margin = new System.Windows.Thickness(0, 0, 5, 0), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 0)), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)) };
            var barR = new System.Windows.Controls.ProgressBar { Name = "BarR_" + device.DeviceNumber, Orientation = System.Windows.Controls.Orientation.Vertical, Minimum = 0, Maximum = 100, Width = 10, Height = 100, Margin = new System.Windows.Thickness(5, 0, 5, 0), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 0)), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)) };
            
            var border = new System.Windows.Controls.Border { Width = 130, Height = 100, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17)), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)), BorderThickness = new System.Windows.Thickness(1) };
            var txt = new System.Windows.Controls.TextBlock { Text = "🎙\n" + device.Name, Foreground = System.Windows.Media.Brushes.DarkGray, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center, FontWeight = System.Windows.FontWeights.Bold, FontSize = 10, TextAlignment = System.Windows.TextAlignment.Center, TextWrapping = System.Windows.TextWrapping.Wrap };
            border.Child = txt;
            
            var sld = new System.Windows.Controls.Slider { Orientation = System.Windows.Controls.Orientation.Vertical, Height = 100, Minimum = 0, Maximum = 1.5, Value = initialLevel, SmallChange = 0.05, LargeChange = 0.1 };
            sld.ValueChanged += (s, e) => { DynamicMicLevels[key] = (float)e.NewValue; };
            
            horiz.Children.Add(barL);
            horiz.Children.Add(border);
            horiz.Children.Add(barR);
            horiz.Children.Add(sld);
            
            var cmb = new System.Windows.Controls.ComboBox { FontSize = 12, Height = 25, Margin = new System.Windows.Thickness(0, 5, 0, 0) };
            cmb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "ON" });
            cmb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "OFF" });
            cmb.SelectedIndex = (initialState == AudioState.OFF) ? 1 : 0;
            cmb.SelectionChanged += (s, e) => { 
                DynamicMicStates[key] = (cmb.SelectedIndex == 1) ? AudioState.OFF : AudioState.ON; 
                SaveCurrentSettings();
            };

            pnl.Children.Add(horiz);
            pnl.Children.Add(cmb);
            
            RegisterName(barL.Name, barL);
            RegisterName(barR.Name, barR);
            
            return pnl;
        }

        #region Source Tile Drag & Drop and Reordering

        private DependencyObject? GetParentSafe(DependencyObject obj)
        {
            if (obj is System.Windows.Media.Visual || obj is System.Windows.Media.Media3D.Visual3D)
            {
                return System.Windows.Media.VisualTreeHelper.GetParent(obj);
            }
            return System.Windows.LogicalTreeHelper.GetParent(obj);
        }

        private bool IsInteractiveElement(DependencyObject? obj)
        {
            while (obj != null && obj != SwitcherPanel && obj != SwitcherScrollViewer)
            {
                if (obj is System.Windows.Controls.Primitives.Thumb ||
                    obj is System.Windows.Controls.Primitives.Track ||
                    obj is System.Windows.Controls.Primitives.RepeatButton ||
                    obj is System.Windows.Controls.Slider ||
                    obj is System.Windows.Controls.Primitives.ScrollBar ||
                    obj is System.Windows.Controls.ComboBox ||
                    obj is System.Windows.Controls.ComboBoxItem ||
                    obj is System.Windows.Controls.CheckBox ||
                    obj is System.Windows.Controls.TextBox ||
                    obj is System.Windows.Controls.Button)
                {
                    return true;
                }
                obj = GetParentSafe(obj);
            }
            return false;
        }

        private FrameworkElement? GetSourceTileFromElement(DependencyObject? obj)
        {
            while (obj != null && obj != SwitcherPanel && obj != SwitcherScrollViewer)
            {
                if (obj is FrameworkElement fe && SwitcherPanel.Children.Contains(fe))
                {
                    return fe;
                }
                obj = GetParentSafe(obj);
            }
            return null;
        }

        private string GetTileIdentifier(FrameworkElement fe)
        {
            if (fe == TileMedia) return "TileMedia";
            if (fe == TileYouTube) return "TileYouTube";
            if (fe == TileNdi) return "TileNdi";
            if (fe == TileHtmlSource) return "TileHtmlSource";
            if (fe == TileColorBars) return "TileColorBars";
            if (fe == TileMatte) return "TileMatte";
            if (fe == TileSystemAudio) return "TileSystemAudio";

            if (fe.DataContext is DeckLinkInputViewModel dl) return "DeckLink:" + dl.DisplayName;
            if (fe.DataContext is ScreenInputViewModel scr) return "Screen:" + scr.DisplayName;
            if (fe.DataContext is WebcamInputViewModel cam) return "Webcam:" + cam.DisplayName;

            if (fe.Tag is string tag && !string.IsNullOrEmpty(tag)) return "Tag:" + tag;
            if (!string.IsNullOrEmpty(fe.Name)) return fe.Name;

            return fe.GetType().Name + "_" + fe.GetHashCode();
        }

        private void SwitcherPanel_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            {
                _potentialDragTile = null;
                return;
            }

            var tile = GetSourceTileFromElement(e.OriginalSource as DependencyObject);
            if (tile != null)
            {
                _dragStartPoint = e.GetPosition(SwitcherPanel);
                _potentialDragTile = tile;
            }
            else
            {
                _potentialDragTile = null;
            }
        }

        private void SwitcherPanel_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_potentialDragTile != null && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && !_isDragging)
            {
                System.Windows.Point pos = e.GetPosition(SwitcherPanel);
                Vector diff = _dragStartPoint - pos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDragging = true;
                    _draggedTile = _potentialDragTile;
                    _potentialDragTile = null;

                    _draggedTile.Opacity = 0.55;

                    try
                    {
                        DataObject dragData = new DataObject("DeckLinkSwitcherSourceTile", _draggedTile);
                        DragDrop.DoDragDrop(SwitcherPanel, dragData, DragDropEffects.Move);
                    }
                    catch (Exception ex)
                    {
                        Log("DragDrop error: " + ex.Message);
                    }
                    finally
                    {
                        _autoScrollTimer.Stop();
                        _scrollDelta = 0;
                        if (_draggedTile != null)
                        {
                            _draggedTile.Opacity = 1.0;
                            _draggedTile = null;
                        }
                        _isDragging = false;
                        SaveSourceOrder();
                    }
                }
            }
        }

        private void SwitcherPanel_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _potentialDragTile = null;
        }

        private void SwitcherPanel_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            System.Windows.Input.Mouse.SetCursor(System.Windows.Input.Cursors.SizeAll);
            e.Handled = true;
        }

        private void SwitcherPanel_DragOver(object sender, DragEventArgs e)
        {
            if (_draggedTile == null) return;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Auto-scrolling near top/bottom edges of SwitcherScrollViewer
            System.Windows.Point scrollPos = e.GetPosition(SwitcherScrollViewer);
            double scrollMargin = 50.0;
            if (scrollPos.Y >= 0 && scrollPos.Y < scrollMargin)
            {
                _scrollDelta = -14;
                if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
            }
            else if (scrollPos.Y > SwitcherScrollViewer.ActualHeight - scrollMargin && scrollPos.Y <= SwitcherScrollViewer.ActualHeight + 40)
            {
                _scrollDelta = 14;
                if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
            }
            else
            {
                _scrollDelta = 0;
                _autoScrollTimer.Stop();
            }

            // Real-time visual reordering
            try
            {
                System.Windows.Point panelPos = e.GetPosition(SwitcherPanel);
                for (int i = 0; i < SwitcherPanel.Children.Count; i++)
                {
                    if (SwitcherPanel.Children[i] is FrameworkElement target && target != _draggedTile)
                    {
                        System.Windows.Media.GeneralTransform transform = target.TransformToAncestor(SwitcherPanel);
                        System.Windows.Rect bounds = transform.TransformBounds(new System.Windows.Rect(0, 0, target.ActualWidth, target.ActualHeight));
                        if (bounds.Contains(panelPos))
                        {
                            int oldIndex = SwitcherPanel.Children.IndexOf(_draggedTile);
                            if (oldIndex != i && oldIndex >= 0)
                            {
                                SwitcherPanel.Children.RemoveAt(oldIndex);
                                SwitcherPanel.Children.Insert(i, _draggedTile);
                            }
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private void SwitcherPanel_Drop(object sender, DragEventArgs e)
        {
            _autoScrollTimer.Stop();
            _scrollDelta = 0;
            if (_draggedTile != null)
            {
                _draggedTile.Opacity = 1.0;
                _draggedTile = null;
            }
            _isDragging = false;
            e.Handled = true;
            SaveSourceOrder();
        }

        private void AutoScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (_isDragging && _scrollDelta != 0)
            {
                SwitcherScrollViewer.ScrollToVerticalOffset(SwitcherScrollViewer.VerticalOffset + _scrollDelta);
            }
        }

        public void SaveSourceOrder()
        {
            try
            {
                var settings = AppSettings.Load();
                settings.SourceOrder = SwitcherPanel.Children
                    .OfType<FrameworkElement>()
                    .Select(GetTileIdentifier)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToList();
                settings.Save();
            }
            catch (Exception ex)
            {
                Log("SaveSourceOrder error: " + ex.Message);
            }
        }

        public void ApplySavedSourceOrder()
        {
            try
            {
                var settings = AppSettings.Load();
                var order = settings.SourceOrder;
                if (order == null || order.Count == 0) return;

                var currentChildren = SwitcherPanel.Children.Cast<UIElement>().ToList();
                if (currentChildren.Count <= 1) return;

                var childMap = new Dictionary<UIElement, string>();
                foreach (var child in currentChildren)
                {
                    if (child is FrameworkElement fe)
                        childMap[child] = GetTileIdentifier(fe);
                }

                var sorted = currentChildren
                    .Select((element, originalIndex) =>
                    {
                        string id = childMap.TryGetValue(element, out var v) ? v : "";
                        int targetIndex = order.IndexOf(id);
                        return new
                        {
                            Element = element,
                            SortKey = targetIndex >= 0 ? targetIndex : 10000 + originalIndex
                        };
                    })
                    .OrderBy(x => x.SortKey)
                    .Select(x => x.Element)
                    .ToList();

                bool orderChanged = false;
                for (int i = 0; i < currentChildren.Count; i++)
                {
                    if (currentChildren[i] != sorted[i])
                    {
                        orderChanged = true;
                        break;
                    }
                }

                if (orderChanged)
                {
                    SwitcherPanel.Children.Clear();
                    foreach (var el in sorted)
                    {
                        SwitcherPanel.Children.Add(el);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("ApplySavedSourceOrder error: " + ex.Message);
            }
        }

        #endregion

        private static double _syntheticAudioPhase = 0;

        public static IntPtr MixAudio(uint audioSampleCount, int activeSourceType, string activeInputName, string caller)
        {
            int totalSamples = (int)audioSampleCount * 2;
            MixerSampleDemand.AddOrUpdate(caller, totalSamples, (_, current) => current + totalSamples);
            int[] mixed = new int[totalSamples];

            Action<string, float, AudioState, bool> mixSource = (name, level, state, isLocalActive) =>
            {
                bool shouldMix = (state == AudioState.ON) || (state == AudioState.AFV && isLocalActive);

                if (name == "Color Bars")
                {
                    if (!shouldMix) return;
                    for (int i = 0; i < totalSamples; i += 2)
                    {
                        // Standard -20 dBFS level (approx 3277 amplitude), ignoring the UI slider
                        short sample = (short)(Math.Sin(_syntheticAudioPhase) * 3277);
                        mixed[i] += sample;
                        mixed[i+1] += sample;
                        _syntheticAudioPhase += 2.0 * Math.PI * 1000.0 / 48000.0;
                        if (_syntheticAudioPhase >= 2.0 * Math.PI) _syntheticAudioPhase -= 2.0 * Math.PI;
                    }
                    return;
                }

                if (AudioFifos.TryGetValue(name, out var q))
                {
                    if (shouldMix)
                    {
                        q.MixInto(mixed, totalSamples, level);
                    }
                    else
                    {
                        q.Drain(totalSamples);
                    }
                }
            };

            foreach (var input in DynamicInputs)
            {
                mixSource(input.DisplayName, (float)input.AudioLevel, input.State, activeSourceType == 0 && activeInputName == input.DisplayName);
            }
            mixSource("Media", MediaAudioLevel, StateMedia, activeSourceType == 3);
            mixSource("YouTube", YouTubeAudioLevel, StateYouTube, activeSourceType == 6);
            mixSource("NDI", NdiAudioLevel, StateNdi, activeSourceType == 8);
            mixSource("Color Bars", ColorBarsAudioLevel, StateColorBars, activeSourceType == 1);
            foreach (var cam in DynamicWebcams)
            {
                mixSource(cam.DisplayName, (float)cam.AudioLevel, cam.State, activeSourceType == 5 && activeInputName == cam.DisplayName);
            }
            mixSource("System Audio", SystemAudioLevel, StateSystemAudio, activeSourceType == 4);
            foreach (var kvp in DynamicMicLevels)
            {
                string key = kvp.Key;
                float level = kvp.Value;
                AudioState st = AudioState.ON;
                if (DynamicMicStates.ContainsKey(key)) st = DynamicMicStates[key];
                mixSource(key, level, st, false);
            }

            LogAudioDiagnostics();

            int maxL = 0, maxR = 0;
            IntPtr outBuffer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(totalSamples * 2);
            unsafe
            {
                short* dstPtr = (short*)outBuffer.ToPointer();
                for (int i = 0; i < totalSamples; i += 2)
                {
                    float finalL = mixed[i] * PgmAudioLevel;
                    if (finalL > 32767) finalL = 32767;
                    else if (finalL < -32768) finalL = -32768;
                    dstPtr[i] = (short)finalL;
                    
                    float finalR = mixed[i+1] * PgmAudioLevel;
                    if (finalR > 32767) finalR = 32767;
                    else if (finalR < -32768) finalR = -32768;
                    dstPtr[i+1] = (short)finalR;

                    int valL = Math.Abs((int)dstPtr[i]);
                    int valR = Math.Abs((int)dstPtr[i+1]);
                    if (valL > maxL) maxL = valL;
                    if (valR > maxR) maxR = valR;
                }
            }
            
            int volL = (maxL * 100) / 32768;
            int volR = (maxR * 100) / 32768;
            
            if (activeSourceType == 1 || activeSourceType == 3)
            {
                MainWindow.Log($"MixAudio ({activeInputName}) MaxL: {maxL}, MaxR: {maxR}, VolL: {volL}, PgmLevel: {PgmAudioLevel}");
            }
            
            Application.Current.Dispatcher.BeginInvoke(() => {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    mw.AudioOutputL.Value = volL;
                    mw.AudioOutputR.Value = volR;
                }
            });
            
            return outBuffer;
        }

        private static long _nextAudioDiagnosticsTick;

        private static void LogAudioDiagnostics()
        {
            long now = Environment.TickCount64;
            long next = Interlocked.Read(ref _nextAudioDiagnosticsTick);
            if (now < next || Interlocked.CompareExchange(ref _nextAudioDiagnosticsTick, now + 2000, next) != next) return;

            foreach (var kvp in AudioFifos)
            {
                if (kvp.Key.StartsWith("Mic_", StringComparison.Ordinal))
                {
                    Log($"Audio diagnostic {kvp.Key}: {kvp.Value.GetDiagnostics()}");
                }
            }

            Log("Mixer demand: " + string.Join(", ", MixerSampleDemand.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        }

        private void CmbMatteColor_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CmbMatteColor.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                string color = item.Content?.ToString();
                switch (color)
                {
                    case "White":   MatteY = 235; MatteU = 128; MatteV = 128; break;
                    case "Yellow":  MatteY = 210; MatteU = 44;  MatteV = 171; break;
                    case "Cyan":    MatteY = 170; MatteU = 156; MatteV = 44; break;
                    case "Green":   MatteY = 145; MatteU = 74;  MatteV = 84; break;
                    case "Magenta": MatteY = 106; MatteU = 182; MatteV = 172; break;
                    case "Red":     MatteY = 81;  MatteU = 98;  MatteV = 212; break;
                    case "Blue":    MatteY = 41;  MatteU = 212; MatteV = 114; break;
                    case "Black":   MatteY = 16;  MatteU = 128; MatteV = 128; break;
                }
                UpdateMiniPreviewsSynthetic();
                if (_activeSourceType == 2) UpdatePgmPreviewSynthetic();
            }
        }

        private static readonly byte[,] SyntheticBarsUyvy = new byte[8, 4] {
            { 128, 235, 128, 235 }, { 44, 210, 171, 210 }, { 156, 170, 44, 170 }, { 74, 145, 84, 145 },
            { 182, 106, 172, 106 }, { 98, 81, 212, 81 }, { 212, 41, 114, 41 }, { 128, 16, 128, 16 }
        };

        /// <summary>Builds a 1080p UYVY frame for Color Bars (1) or Matte (2) for software outputs (NDI/SRT/etc).</summary>
        private static byte[] BuildSyntheticUyvy1080(int sourceType)
        {
            const int w = 1920, h = 1080, rowBytes = w * 2;
            byte[] frame = new byte[rowBytes * h];
            if (sourceType == 2)
            {
                for (int i = 0; i < frame.Length; i += 4)
                {
                    frame[i] = MatteU; frame[i + 1] = MatteY; frame[i + 2] = MatteV; frame[i + 3] = MatteY;
                }
            }
            else
            {
                for (int x = 0; x < w; x += 2)
                {
                    int bar = (x * 8) / w;
                    int o = x * 2;
                    frame[o] = SyntheticBarsUyvy[bar, 0]; frame[o + 1] = SyntheticBarsUyvy[bar, 1];
                    frame[o + 2] = SyntheticBarsUyvy[bar, 2]; frame[o + 3] = SyntheticBarsUyvy[bar, 3];
                }
                for (int y = 1; y < h; y++) Buffer.BlockCopy(frame, 0, frame, y * rowBytes, rowBytes);
            }
            return frame;
        }

        private void UpdatePgmPreviewSynthetic()
        {
            if (_activeSourceType == 0 || _bmpOutput == null) return;

            byte[] pixels = new byte[480 * 270 * 4];
            
            if (_activeSourceType == 2) // Matte
            {
                byte r=0, g=0, b=0;
                string color = "";
                Application.Current.Dispatcher.Invoke(() => {
                    if (CmbMatteColor.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                        color = item.Content?.ToString();
                });
                
                switch (color)
                {
                    case "White":   r=255; g=255; b=255; break;
                    case "Yellow":  r=255; g=255; b=0;   break;
                    case "Cyan":    r=0;   g=255; b=255; break;
                    case "Green":   r=0;   g=255; b=0;   break;
                    case "Magenta": r=255; g=0;   b=255; break;
                    case "Red":     r=255; g=0;   b=0;   break;
                    case "Blue":    r=0;   g=0;   b=255; break;
                    case "Black":   r=0;   g=0;   b=0;   break;
                }
                
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = b; // B
                    pixels[i+1] = g; // G
                    pixels[i+2] = r; // R
                    pixels[i+3] = 255; // A
                }
            }
            else if (_activeSourceType == 3 || _activeSourceType == 4 || _activeSourceType == 5 || _activeSourceType == 6 || _activeSourceType == 7 || _activeSourceType == 8) // Media, Desktop, Webcam, YouTube, HTML, or NDI
            {
                // Handled continuously by streaming/capture callbacks
                return;
            }
            else if (_activeSourceType == 1) // Color Bars
            {
                byte[,] colors = new byte[8, 3] {
                    { 255, 255, 255 }, // White
                    { 0, 255, 255 },   // Yellow
                    { 255, 255, 0 },   // Cyan
                    { 0, 255, 0 },     // Green
                    { 255, 0, 255 },   // Magenta
                    { 0, 0, 255 },     // Red
                    { 255, 0, 0 },     // Blue
                    { 0, 0, 0 }        // Black
                };
                
                for (int y = 0; y < 270; y++)
                {
                    for (int x = 0; x < 480; x++)
                    {
                        int barIndex = (x * 8) / 480;
                        int offset = (y * 480 + x) * 4;
                        pixels[offset] = colors[barIndex, 0]; // B
                        pixels[offset+1] = colors[barIndex, 1]; // G
                        pixels[offset+2] = colors[barIndex, 2]; // R
                        pixels[offset+3] = 255; // A
                    }
                }
            }

            // Store directly (no UI thread needed) so stream loop has it immediately
            

            Application.Current.Dispatcher.BeginInvoke(() => {
                if (_bmpOutput != null)
                {
                    _bmpOutput.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), pixels, 480 * 4, 0);
                }
            });
        }


        private void UpdateMiniPreviewsSynthetic()
        {
            // Color Bars (Type 1)
            byte[] cbPixels = new byte[480 * 270 * 4];
            byte[,] colors = new byte[8, 3] {
                { 255, 255, 255 }, { 0, 255, 255 }, { 255, 255, 0 }, { 0, 255, 0 },
                { 255, 0, 255 }, { 0, 0, 255 }, { 255, 0, 0 }, { 0, 0, 0 }
            };
            for (int y = 0; y < 270; y++) {
                for (int x = 0; x < 480; x++) {
                    int barIndex = (x * 8) / 480;
                    int offset = (y * 480 + x) * 4;
                    cbPixels[offset] = colors[barIndex, 0];
                    cbPixels[offset+1] = colors[barIndex, 1];
                    cbPixels[offset+2] = colors[barIndex, 2];
                    cbPixels[offset+3] = 255;
                }
            }
            
            // Matte (Type 2)
            byte[] mPixels = new byte[480 * 270 * 4];
            byte r=0, g=0, b=0;
            string color = "";
            Application.Current.Dispatcher.Invoke(() => {
                if (CmbMatteColor.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                    color = item.Content?.ToString();
            });
            switch (color)
            {
                case "White":   r=255; g=255; b=255; break;
                case "Yellow":  r=255; g=255; b=0;   break;
                case "Cyan":    r=0;   g=255; b=255; break;
                case "Green":   r=0;   g=255; b=0;   break;
                case "Magenta": r=255; g=0;   b=255; break;
                case "Red":     r=255; g=0;   b=0;   break;
                case "Blue":    r=0;   g=0;   b=255; break;
                case "Black":   r=0;   g=0;   b=0;   break;
            }
            for (int i = 0; i < mPixels.Length; i += 4) {
                mPixels[i] = b; mPixels[i+1] = g; mPixels[i+2] = r; mPixels[i+3] = 255;
            }

            // Desktop (Type 4)
            // Desktop preview is updated independently by background thread.

            Application.Current.Dispatcher.BeginInvoke(() => {
                if (_bmpColorBars != null) _bmpColorBars.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), cbPixels, 480 * 4, 0);
                if (_bmpMatte != null) _bmpMatte.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), mPixels, 480 * 4, 0);
            });
        }


        public static void OutputProgramAudioAndVideo(IntPtr modifiedAudioBuffer, uint audioSampleCount, byte[] uyvyBytes, bool writeSystemAudio = true)
        {
            if (modifiedAudioBuffer != IntPtr.Zero && audioSampleCount > 0)
            {
            if (writeSystemAudio) SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, audioSampleCount);
                if (YtStreamer.IsStreaming || LclRecorder.IsRecording || NdiOut.IsRunning || SrtOut.IsStreaming)
                {
                    byte[] audBytes = new byte[audioSampleCount * 4];
                    System.Runtime.InteropServices.Marshal.Copy(modifiedAudioBuffer, audBytes, 0, audBytes.Length);
                    if (YtStreamer.IsStreaming) YtStreamer.PushFrame(audBytes, uyvyBytes);
                    if (LclRecorder.IsRecording) LclRecorder.PushFrame(audBytes, uyvyBytes);
                    if (NdiOut.IsRunning) NdiOut.PushFrame(audBytes, uyvyBytes);
                    if (SrtOut.IsStreaming) SrtOut.PushFrame(audBytes, uyvyBytes);
                }
            }
        }

        internal static byte[] ConvertBgraToUyvy1080(byte[] bgra)
        {
            const int srcW = 480, dstW = 1920, dstH = 1080;
            byte[] uyvy = new byte[dstH * dstW * 2];
            Parallel.For(0, dstH, y =>
            {
                int srcY = _uyvyRowLut[y];
                int dstRow = y * dstW * 2;
                int srcRow = srcY * srcW * 4;
                for (int x = 0; x < dstW; x += 2)
                {
                    int o1 = srcRow + _uyvyColLut[x] * 4;
                    byte b1 = bgra[o1], g1 = bgra[o1+1], r1 = bgra[o1+2];
                    int o2 = srcRow + _uyvyColLut[x+1] * 4;
                    byte b2 = bgra[o2], g2 = bgra[o2+1], r2 = bgra[o2+2];
                    int Y1=((66*r1+129*g1+25*b1+128)>>8)+16, U1=((-38*r1-74*g1+112*b1+128)>>8)+128, V1=((112*r1-94*g1-18*b1+128)>>8)+128, Y2=((66*r2+129*g2+25*b2+128)>>8)+16;
                    if (Y1<16) Y1=16; else if (Y1>235) Y1=235;
                    if (Y2<16) Y2=16; else if (Y2>235) Y2=235;
                    if (U1<16) U1=16; else if (U1>240) U1=240;
                    if (V1<16) V1=16; else if (V1>240) V1=240;
                    int d = dstRow + x*2;
                    uyvy[d]=(byte)U1; uyvy[d+1]=(byte)Y1; uyvy[d+2]=(byte)V1; uyvy[d+3]=(byte)Y2;
                }
            });
            return uyvy;
        }

        internal static unsafe byte[] ConvertBgra1080ToUyvy1080(byte[] bgra)
        {
            int dstW = 1920, dstH = 1080;
            byte[] uyvy = new byte[dstH * dstW * 2];
            fixed (byte* pBgra = bgra)
            fixed (byte* pUyvy = uyvy)
            {
                IntPtr ptrBgra = (IntPtr)pBgra;
                IntPtr ptrUyvy = (IntPtr)pUyvy;
                Parallel.For(0, dstH, y =>
                {
                    byte* srcBase = (byte*)ptrBgra;
                    byte* dstBase = (byte*)ptrUyvy;
                    int rowOffset = y * dstW * 4;
                    int dstRow = y * dstW * 2;
                    byte* srcRow = srcBase + rowOffset;
                    byte* dstRowPtr = dstBase + dstRow;
                    for (int x = 0; x < dstW; x += 2)
                    {
                        byte b1 = srcRow[x * 4];
                        byte g1 = srcRow[x * 4 + 1];
                        byte r1 = srcRow[x * 4 + 2];
                        
                        byte b2 = srcRow[x * 4 + 4];
                        byte g2 = srcRow[x * 4 + 5];
                        byte r2 = srcRow[x * 4 + 6];
                        
                        int rAvg = (r1 + r2) >> 1;
                        int gAvg = (g1 + g2) >> 1;
                        int bAvg = (b1 + b2) >> 1;

                        int Y1 = ((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16;
                        int U1 = ((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                        int V1 = ((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128;
                        int Y2 = ((66 * r2 + 129 * g2 + 25 * b2 + 128) >> 8) + 16;

                        if (Y1 < 16) Y1 = 16; else if (Y1 > 235) Y1 = 235;
                        if (Y2 < 16) Y2 = 16; else if (Y2 > 235) Y2 = 235;
                        if (U1 < 16) U1 = 16; else if (U1 > 240) U1 = 240;
                        if (V1 < 16) V1 = 16; else if (V1 > 240) V1 = 240;

                        dstRowPtr[x * 2] = (byte)U1;
                        dstRowPtr[x * 2 + 1] = (byte)Y1;
                        dstRowPtr[x * 2 + 2] = (byte)V1;
                        dstRowPtr[x * 2 + 3] = (byte)Y2;
                    }
                });
            }
            return uyvy;
        }

        private void StartDesktopCaptureLoop()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        for (int i = 0; i < DynamicScreens.Count; i++)
                        {
                            var vm = DynamicScreens[i];
                            var screen = vm.Screen;
                            var ctx = vm.Context;
                            int screenWidth = screen.Bounds.Width;
                            int screenHeight = screen.Bounds.Height;

                            if (ctx.ScreenBmp == null || ctx.ScreenBmp.Width != screenWidth || ctx.ScreenBmp.Height != screenHeight)
                            {
                                ctx.Dispose();
                                ctx.ScreenBmp = new System.Drawing.Bitmap(screenWidth, screenHeight);
                                ctx.ScreenG = System.Drawing.Graphics.FromImage(ctx.ScreenBmp);

                                ctx.Prev480Bmp = new System.Drawing.Bitmap(480, 270);
                                ctx.Prev480G = System.Drawing.Graphics.FromImage(ctx.Prev480Bmp);
                                ctx.Prev480G.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;

                                ctx.Pgm1080Bmp = new System.Drawing.Bitmap(1920, 1080);
                                ctx.Pgm1080G = System.Drawing.Graphics.FromImage(ctx.Pgm1080Bmp);
                                ctx.Pgm1080G.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            }

                            // Capture screen
                            ctx.ScreenG.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, ctx.ScreenBmp.Size, System.Drawing.CopyPixelOperation.SourceCopy);

                            // Draw preview (480x270)
                            ctx.Prev480G.Clear(System.Drawing.Color.Black);
                            float prevScale = Math.Min(480f / screenWidth, 270f / screenHeight);
                            int pDrawW = (int)(screenWidth * prevScale);
                            int pDrawH = (int)(screenHeight * prevScale);
                            int pDrawX = (480 - pDrawW) / 2;
                            int pDrawY = (270 - pDrawH) / 2;
                            ctx.Prev480G.DrawImage(ctx.ScreenBmp, pDrawX, pDrawY, pDrawW, pDrawH);

                            var data480 = ctx.Prev480Bmp.LockBits(new System.Drawing.Rectangle(0, 0, 480, 270), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            System.Runtime.InteropServices.Marshal.Copy(data480.Scan0, ctx.Prev480Buffer, 0, ctx.Prev480Buffer.Length);
                            ctx.Prev480Bmp.UnlockBits(data480);

                            Application.Current.Dispatcher.BeginInvoke(() => {
                                vm.PreviewBitmap?.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), ctx.Prev480Buffer, 480 * 4, 0);
                            });

                            // If this screen is currently active on Program Output
                            if (_activeSourceType == 4 && _activeScreen == vm)
                            {
                                if (screenWidth == 1920 && screenHeight == 1080)
                                {
                                    var dataScreen = ctx.ScreenBmp.LockBits(new System.Drawing.Rectangle(0, 0, 1920, 1080), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                    System.Runtime.InteropServices.Marshal.Copy(dataScreen.Scan0, ctx.Pgm1080Buffer, 0, ctx.Pgm1080Buffer.Length);
                                    ctx.ScreenBmp.UnlockBits(dataScreen);
                                }
                                else
                                {
                                    ctx.Pgm1080G.Clear(System.Drawing.Color.Black);
                                    float pgmScale = Math.Min(1920f / screenWidth, 1080f / screenHeight);
                                    int gDrawW = (int)(screenWidth * pgmScale);
                                    int gDrawH = (int)(screenHeight * pgmScale);
                                    int gDrawX = (1920 - gDrawW) / 2;
                                    int gDrawY = (1080 - gDrawH) / 2;
                                    ctx.Pgm1080G.DrawImage(ctx.ScreenBmp, gDrawX, gDrawY, gDrawW, gDrawH);

                                    var data1080 = ctx.Pgm1080Bmp.LockBits(new System.Drawing.Rectangle(0, 0, 1920, 1080), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                    System.Runtime.InteropServices.Marshal.Copy(data1080.Scan0, ctx.Pgm1080Buffer, 0, ctx.Pgm1080Buffer.Length);
                                    ctx.Pgm1080Bmp.UnlockBits(data1080);
                                }

                                lock (_pgmLock)
                                {
                                    if (_latestPgmFrame == null || _latestPgmFrame.Length != 1920 * 1080 * 4)
                                        _latestPgmFrame = new byte[1920 * 1080 * 4];
                                    Array.Copy(ctx.Pgm1080Buffer, _latestPgmFrame, _latestPgmFrame.Length);
                                }

                                Application.Current.Dispatcher.BeginInvoke(() => {
                                    if (_bmpOutput != null)
                                        _bmpOutput.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), ctx.Prev480Buffer, 480 * 4, 0);
                                });
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(40);
                }
            });
        }

        // Pre-computed lookup tables for fast BGRA->UYVY scaling 480x270 -> 1920x1080
        private static readonly int[] _uyvyRowLut = BuildRowLut();
        private static readonly int[] _uyvyColLut = BuildColLut();
        private static int[] BuildRowLut() { var t = new int[1080]; for (int y = 0; y < 1080; y++) t[y] = Math.Min((int)(y * 270.0f / 1080f), 269); return t; }
        private static int[] BuildColLut() { var t = new int[1920]; for (int x = 0; x < 1920; x++) t[x] = Math.Min((int)(x * 480.0f / 1920f), 479); return t; }

        public static _BMDDisplayMode ParseDisplayMode(string formatStr)
        {
            if (string.IsNullOrEmpty(formatStr)) return _BMDDisplayMode.bmdModeHD1080i50;
            if (formatStr.Contains("PAL")) return _BMDDisplayMode.bmdModePAL;
            if (formatStr.Contains("1080p")) return _BMDDisplayMode.bmdModeHD1080p25;
            if (formatStr.Contains("720p")) return _BMDDisplayMode.bmdModeHD720p50;
            return _BMDDisplayMode.bmdModeHD1080i50;
        }

        private static byte[]? _softwareDeckLinkUyvyBuffer;
        private static readonly object _softwareDeckLinkLock = new object();

        private static void ScheduleSoftwareDeckLinkFrame(IDeckLinkVideoInputFrame inputFrame, IDeckLinkAudioInputPacket audioPacket, float inputVolume)
        {
            int width = inputFrame.GetWidth();
            int height = inputFrame.GetHeight();
            int rowBytes = inputFrame.GetRowBytes();

            uint audioSampleCount = audioPacket != null ? (uint)audioPacket.GetSampleFrameCount() : 1920;
            IntPtr modifiedAudioBuffer = MixAudio(audioSampleCount, ActiveSourceType, ActiveInputName, "software-decklink-output");

            byte[]? uyvyBytes = null;
            if (YtStreamer.IsStreaming || LclRecorder.IsRecording || NdiOut.IsRunning || SrtOut.IsStreaming)
            {
                lock (_softwareDeckLinkLock)
                {
                    if (_softwareDeckLinkUyvyBuffer == null || _softwareDeckLinkUyvyBuffer.Length != 1920 * 1080 * 2)
                    {
                        _softwareDeckLinkUyvyBuffer = new byte[1920 * 1080 * 2];
                    }

                    var inputBuf = (IDeckLinkVideoBuffer)inputFrame;
                    inputBuf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                    try
                    {
                        inputBuf.GetBytes(out IntPtr inputBuffer);
                        unsafe
                        {
                            fixed (byte* dest = _softwareDeckLinkUyvyBuffer)
                            {
                                if (width == 1920 && height == 1080)
                                {
                                    DeckLinkDevice.memcpy((IntPtr)dest, inputBuffer, new UIntPtr(1920 * 1080 * 2));
                                }
                                else
                                {
                                    DeckLinkDevice.ScaleUyvyTo1080Uyvy((byte*)inputBuffer, width, height, rowBytes, dest, 1920 * 2);
                                }
                                ApplyLogoOverlayUyvy(dest);
                            }
                        }
                    }
                    finally
                    {
                        inputBuf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                    }

                    uyvyBytes = (byte[])_softwareDeckLinkUyvyBuffer.Clone();
                }
            }

            if (modifiedAudioBuffer != IntPtr.Zero)
            {
                OutputProgramAudioAndVideo(modifiedAudioBuffer, audioSampleCount, uyvyBytes, true);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
            }
        }

        private void InitializeDynamicRouting(DeckLinkDeviceInfo outInfo, System.Collections.Generic.List<SelectedInputConfig> selectedInputs, System.Windows.Media.Imaging.WriteableBitmap bmpOutput)
        {
            List<IDeckLink> mtaLinks = new List<IDeckLink>();
            try
            {
                IDeckLinkIterator iterator = new CDeckLinkIterator();
                while (true)
                {
                    iterator.Next(out IDeckLink dl);
                    if (dl == null) break;
                    mtaLinks.Add(dl);
                }
            }
            catch (Exception ex)
            {
                Log("MTA CDeckLinkIterator error: " + ex.Message);
            }

            IDeckLink GetLink(DeckLinkDeviceInfo info)
            {
                if (info == null || info.Index < 0 || info.Index >= mtaLinks.Count) return null;
                return mtaLinks[info.Index];
            }

            IDeckLink outLink = GetLink(outInfo);
            if (outLink != null)
            {
                _activeOutput = new DeckLinkDevice(outLink, "Output");
                _activeOutput.StartPlayback();
            }
            else
            {
                _activeOutput = null;
                Log("No physical output selected (None).");
            }
            
            Application.Current.Dispatcher.Invoke(() => DynamicInputs.Clear());
            _activeInput = null;

            var settings = AppSettings.Load();

            int inputIndex = 1;
            foreach (var inCfg in selectedInputs)
            {
                var roleName = "Input " + inputIndex;
                inputIndex++;
                
                var dl = GetLink(inCfg.Device);
                if (dl == null) continue; // Skip if 'None' or invalid
                
                if (outLink != null && dl == outLink) continue; // Skip output device

                DeckLinkInputViewModel vm = null;
                Application.Current.Dispatcher.Invoke(() => {
                    vm = new DeckLinkInputViewModel(roleName);
                    vm.PreviewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    
                    if (settings.InputAudioLevels.ContainsKey(roleName)) vm.AudioLevel = settings.InputAudioLevels[roleName];
                    if (settings.InputAudioStates.ContainsKey(roleName)) vm.AudioStateIndex = (int)settings.InputAudioStates[roleName];
                    
                    DynamicInputs.Add(vm);
                    
                    var template = (DataTemplate)Application.Current.MainWindow.FindResource("InputTileTemplate");
                    var tile = (UIElement)template.LoadContent();
                    if (tile is FrameworkElement fe) fe.DataContext = vm;
                    
                    var window = (MainWindow)Application.Current.MainWindow;
                    window.SwitcherPanel.Children.Insert(DynamicInputs.Count - 1, tile);
                });

                var inputDev = new DeckLinkDevice(dl, roleName);
                vm.Device = inputDev;
                
                inputDev.PreviewBitmap = vm.PreviewBitmap;
                inputDev.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == inputDev)
                    {
                        if (_activeOutput != null)
                        {
                            _activeOutput.ScheduleFrame(frame, audio, inputDev.AudioLevel);
                        }
                        else
                        {
                            ScheduleSoftwareDeckLinkFrame(frame, audio, inputDev.AudioLevel);
                        }
                    }
                };
                inputDev.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == inputDev)
                    {
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                    }
                };
                inputDev.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { vm.AudioL = l; vm.AudioR = r; }); };

                string formatStr = inCfg.Format ?? "Auto (Detect)";
                bool autoDetect = formatStr.StartsWith("Auto");
                _BMDDisplayMode mode = ParseDisplayMode(formatStr);
                inputDev.StartCapture(mode, autoDetect);
                
                if (_activeInput == null) _activeInput = inputDev;
            }

            _syntheticCts?.Cancel();
            _syntheticCts = new CancellationTokenSource();
            var token = _syntheticCts.Token;

            Task.Run(async () =>
            {
                double syntheticAudioPhase = 0;
                long frameIntervalTicks = Stopwatch.Frequency / 25;
                long nextFrameTick = Stopwatch.GetTimestamp();
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_activeSourceType == 1 || _activeSourceType == 2 || _activeSourceType == 4 || _activeSourceType == 5 || _activeSourceType == 6 || _activeSourceType == 7 || _activeSourceType == 8)
                        {
                            // Keep _latestPgmFrame fresh for streaming loop
                            UpdatePgmPreviewSynthetic();

                            if (_activeOutput != null)
                            {
                                _activeOutput.ScheduleSyntheticFrame(_activeSourceType);
                            }
                            else
                            {
                                uint audioSampleCount = 1920;
                                IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName, "synthetic-software-output");
                                if (modifiedAudioBuffer != IntPtr.Zero)
                                {
                                    byte[] uyvyBytes = null;
                                    if (_activeSourceType == 6 && LatestYouTubeUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestYouTubeUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 8 && LatestNdiUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestNdiUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 7 && LatestHtmlSourceUyvy != null)
                                    {
                                        lock (MainWindow._pgmLock)
                                        {
                                            uyvyBytes = (byte[])LatestHtmlSourceUyvy.Clone();
                                        }
                                    }
                                    else if (_activeSourceType == 1 || _activeSourceType == 2)
                                    {
                                        uyvyBytes = BuildSyntheticUyvy1080(_activeSourceType);
                                    }
                                    else
                                    {
                                        lock (MainWindow._pgmLock) 
                                        { 
                                            if (_latestPgmFrame != null && _latestPgmFrame.Length == 1920 * 1080 * 4) uyvyBytes = ConvertBgra1080ToUyvy1080(_latestPgmFrame);
                                            else if (_latestPgmFrame != null) uyvyBytes = ConvertBgraToUyvy1080(_latestPgmFrame); 
                                        }
                                    }
                                    ApplyLogoOverlayUyvy(uyvyBytes);
                                    OutputProgramAudioAndVideo(modifiedAudioBuffer, audioSampleCount, uyvyBytes);
                                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                                }
                            }
                        }
                        nextFrameTick += frameIntervalTicks;
                        long remainingTicks = nextFrameTick - Stopwatch.GetTimestamp();
                        if (remainingTicks > 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency), token);
                        }
                        else
                        {
                            nextFrameTick = Stopwatch.GetTimestamp();
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, token);

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplySavedSourceOrder();
                SwitcherPanel.IsEnabled = true;
                CmbOutput.IsEnabled = true;
                InputSelectorsControl.IsEnabled = true;
                BtnApplySettings.IsEnabled = false;
                bool hasAnyHardware = _activeOutput != null || DynamicInputs.Count > 0;
                TxtStatus.Text = hasAnyHardware ? "Running!" : "Running (None selected)";
                if (_activeInput == null && _activeSourceType == 0)
                {
                    ClearPgmPreview();
                }
            }));
        }

        protected override void OnClosed(EventArgs e)
        {
            try { NdiOut.Stop(); } catch { }
            try { SrtOut.Stop(); } catch { }
            base.OnClosed(e);
        }
    }

    public class SelectedInputConfig
    {
        public DeckLinkDeviceInfo Device { get; set; }
        public string Format { get; set; } = "Auto (Detect)";
    }

    public class InputSelectionViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public string Label { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<DeckLinkDeviceInfo> AvailableDevices { get; set; }
        
        private DeckLinkDeviceInfo _selectedDevice;
        public DeckLinkDeviceInfo SelectedDevice 
        { 
            get => _selectedDevice; 
            set { _selectedDevice = value; OnPropertyChanged(nameof(SelectedDevice)); }
        }

        public System.Collections.ObjectModel.ObservableCollection<string> AvailableFormats { get; set; } = new()
        {
            "Auto (Detect)",
            "1080i 50",
            "PAL (576i 50)",
            "1080p 25",
            "720p 50"
        };

        private string _selectedFormat = "Auto (Detect)";
        public string SelectedFormat
        {
            get => _selectedFormat; 
            set { _selectedFormat = value; OnPropertyChanged(nameof(SelectedFormat)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class DeckLinkInputViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public DeckLinkDevice Device { get; set; }
        public string DisplayName { get; set; }
        
        private double _audioLevel = 1.0;
        public double AudioLevel 
        { 
            get => _audioLevel; 
            set { _audioLevel = value; OnPropertyChanged(nameof(AudioLevel)); if (Device != null) Device.AudioLevel = (float)value; ((MainWindow)System.Windows.Application.Current.MainWindow).SaveCurrentSettings(); }
        }

        private int _audioL;
        public int AudioL { get => _audioL; set { _audioL = value; OnPropertyChanged(nameof(AudioL)); } }

        private int _audioR;
        public int AudioR { get => _audioR; set { _audioR = value; OnPropertyChanged(nameof(AudioR)); } }

        private System.Windows.Media.Imaging.WriteableBitmap _previewBitmap;
        public System.Windows.Media.Imaging.WriteableBitmap PreviewBitmap { get => _previewBitmap; set { _previewBitmap = value; OnPropertyChanged(nameof(PreviewBitmap)); } }

        private int _audioStateIndex = 0; // 0=AFV, 1=ON, 2=OFF
        public int AudioStateIndex 
        { 
            get => _audioStateIndex; 
            set { _audioStateIndex = value; OnPropertyChanged(nameof(AudioStateIndex)); ((MainWindow)System.Windows.Application.Current.MainWindow).SaveCurrentSettings(); }
        }
        
        public AudioState State => (AudioState)_audioStateIndex;

        private string _bgColor = "#333344";
        public string BgColor { get => _bgColor; set { _bgColor = value; OnPropertyChanged(nameof(BgColor)); } }
        private string _fgColor = "#ffffff";
        public string FgColor { get => _fgColor; set { _fgColor = value; OnPropertyChanged(nameof(FgColor)); } }

        public RelayCommand SelectInputCmd { get; }

        public DeckLinkInputViewModel(string name)
        {
            DisplayName = name;
            SelectInputCmd = new RelayCommand(_ => {
                MainWindow.SetActiveSource(0, DisplayName, Device);
            });
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action<object> _execute;
        public RelayCommand(Action<object> execute) { _execute = execute; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _execute(parameter);
    }

    public class ScreenCaptureContext : IDisposable
    {
        public System.Drawing.Bitmap? ScreenBmp;
        public System.Drawing.Graphics? ScreenG;
        public System.Drawing.Bitmap? Prev480Bmp;
        public System.Drawing.Graphics? Prev480G;
        public byte[] Prev480Buffer = new byte[480 * 270 * 4];
        public System.Drawing.Bitmap? Pgm1080Bmp;
        public System.Drawing.Graphics? Pgm1080G;
        public byte[] Pgm1080Buffer = new byte[1920 * 1080 * 4];

        public void Dispose()
        {
            ScreenG?.Dispose();
            ScreenBmp?.Dispose();
            Prev480G?.Dispose();
            Prev480Bmp?.Dispose();
            Pgm1080G?.Dispose();
            Pgm1080Bmp?.Dispose();
        }
    }

    public class ScreenInputViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public int ScreenIndex { get; set; }
        public string DisplayName { get; set; }
        public System.Windows.Forms.Screen Screen { get; set; }
        public ScreenCaptureContext Context { get; } = new ScreenCaptureContext();

        private System.Windows.Media.Imaging.WriteableBitmap _previewBitmap;
        public System.Windows.Media.Imaging.WriteableBitmap PreviewBitmap 
        { 
            get => _previewBitmap; 
            set { _previewBitmap = value; OnPropertyChanged(nameof(PreviewBitmap)); } 
        }

        private string _bgColor = "#333344";
        public string BgColor 
        { 
            get => _bgColor; 
            set { _bgColor = value; OnPropertyChanged(nameof(BgColor)); } 
        }
        private string _fgColor = "#ffffff";
        public string FgColor 
        { 
            get => _fgColor; 
            set { _fgColor = value; OnPropertyChanged(nameof(FgColor)); } 
        }

        public RelayCommand SelectInputCmd { get; }

        public ScreenInputViewModel(int index, string displayName, System.Windows.Forms.Screen screen)
        {
            ScreenIndex = index;
            DisplayName = displayName;
            Screen = screen;
            SelectInputCmd = new RelayCommand(_ => {
                var mw = (MainWindow)System.Windows.Application.Current.MainWindow;
                mw.SwitchToScreen(this);
            });
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class WebcamInputViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public WebcamSource Source { get; set; }
        public string DisplayName { get; set; }
        public string DeviceName { get; set; }
        public int WebcamIndex { get; set; }

        private double _audioLevel = 1.0;
        public double AudioLevel 
        { 
            get => _audioLevel; 
            set 
            { 
                _audioLevel = value; 
                OnPropertyChanged(nameof(AudioLevel)); 
                ((MainWindow)System.Windows.Application.Current.MainWindow).SaveCurrentSettings(); 
            }
        }

        private int _audioL;
        public int AudioL { get => _audioL; set { _audioL = value; OnPropertyChanged(nameof(AudioL)); } }

        private int _audioR;
        public int AudioR { get => _audioR; set { _audioR = value; OnPropertyChanged(nameof(AudioR)); } }

        private System.Windows.Media.Imaging.WriteableBitmap _previewBitmap;
        public System.Windows.Media.Imaging.WriteableBitmap PreviewBitmap 
        { 
            get => _previewBitmap; 
            set { _previewBitmap = value; OnPropertyChanged(nameof(PreviewBitmap)); } 
        }

        private int _audioStateIndex = 0; // 0=AFV, 1=ON, 2=OFF
        public int AudioStateIndex 
        { 
            get => _audioStateIndex; 
            set 
            { 
                _audioStateIndex = value; 
                OnPropertyChanged(nameof(AudioStateIndex)); 
                ((MainWindow)System.Windows.Application.Current.MainWindow).SaveCurrentSettings(); 
            }
        }

        public AudioState State => (AudioState)_audioStateIndex;

        private string _bgColor = "#333344";
        public string BgColor 
        { 
            get => _bgColor; 
            set { _bgColor = value; OnPropertyChanged(nameof(BgColor)); } 
        }
        private string _fgColor = "#ffffff";
        public string FgColor 
        { 
            get => _fgColor; 
            set { _fgColor = value; OnPropertyChanged(nameof(FgColor)); } 
        }

        public RelayCommand SelectInputCmd { get; }

        public WebcamInputViewModel(string displayName, string deviceName, int index)
        {
            DisplayName = displayName;
            DeviceName = deviceName;
            WebcamIndex = index;
            SelectInputCmd = new RelayCommand(_ => {
                var mw = (MainWindow)System.Windows.Application.Current.MainWindow;
                mw.SwitchToWebcam(this);
            });
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class DeckLinkDeviceInfo
    {
        public string DisplayName { get; set; }
        public int Index { get; set; }
    }

    public class DeckLinkDevice : IDeckLinkInputCallback
    {
        public string _roleName;
        private IDeckLink _deckLink;
        private IDeckLinkInput _deckLinkInput;
        private IDeckLinkOutput _deckLinkOutput;
        private bool _isCapturing = false;
        private bool _isPlaying = false;
        private int _frameCount = 0;
        
        public bool IsProgramSource { get; set; } = false;
        public float AudioLevel { get; set; } = 1.0f;

        public System.Windows.Media.Imaging.WriteableBitmap PreviewBitmap;
        private int _lastPreviewTicks = 0;
        private byte[] _previewBuffer = new byte[480 * 270 * 4];

        public Action<IDeckLinkVideoInputFrame, IDeckLinkAudioInputPacket> OnVideoAndAudioArrived;
        public Action<int, int> OnAudioLevelArrived;
        public Action<byte[]> OnPreviewBufferUpdated;

        public DeckLinkDevice(IDeckLink deckLink, string roleName)
        {
            _roleName = roleName;
            _deckLink = deckLink;
            _deckLinkInput = (IDeckLinkInput)_deckLink;
            _deckLinkOutput = (IDeckLinkOutput)_deckLink;
        }

        private _BMDDisplayMode _currentDisplayMode = _BMDDisplayMode.bmdModeHD1080i50;
        public _BMDDisplayMode CurrentDisplayMode => _currentDisplayMode;

        private static readonly int[] _palTo1080XMap = BuildPalTo1080XMap();

        private static int[] BuildPalTo1080XMap()
        {
            int[] map = new int[960];
            for (int mp = 0; mp < 960; mp++)
            {
                int x = mp * 2;
                int srcX = (int)((long)x * 720 / 1920);
                if (srcX > 718) srcX = 718;
                map[mp] = (srcX / 2) * 4;
            }
            return map;
        }

        public static unsafe void ScalePalUyvyTo1080Uyvy(byte* src, int srcRowBytes, byte* dest, int destRowBytes)
        {
            Parallel.For(0, 1080, y =>
            {
                int srcY = (y * 576) / 1080;
                byte* srcLine = src + (srcY * srcRowBytes);
                byte* destLine = dest + (y * destRowBytes);

                fixed (int* xMap = _palTo1080XMap)
                {
                    uint* dest32 = (uint*)destLine;
                    for (int mp = 0; mp < 960; mp++)
                    {
                        dest32[mp] = *(uint*)(srcLine + xMap[mp]);
                    }
                }
            });
        }

        public static unsafe void ScaleUyvyTo1080Uyvy(byte* src, int srcW, int srcH, int srcRowBytes, byte* dest, int destRowBytes)
        {
            if (srcW == 720 && srcH == 576)
            {
                ScalePalUyvyTo1080Uyvy(src, srcRowBytes, dest, destRowBytes);
                return;
            }

            Parallel.For(0, 1080, y =>
            {
                int srcY = (y * srcH) / 1080;
                if (srcY >= srcH) srcY = srcH - 1;
                byte* srcLine = src + (srcY * srcRowBytes);
                byte* destLine = dest + (y * destRowBytes);

                uint* dest32 = (uint*)destLine;
                for (int mp = 0; mp < 960; mp++)
                {
                    int x = mp * 2;
                    int srcX = (int)((long)x * srcW / 1920);
                    if (srcX >= srcW - 1) srcX = srcW - 2;
                    dest32[mp] = *(uint*)(srcLine + (srcX / 2) * 4);
                }
            });
        }

        public void StartCapture(_BMDDisplayMode displayMode = _BMDDisplayMode.bmdModeHD1080i50, bool enableFormatDetection = true)
        {
            if (_isCapturing) return;

            _currentDisplayMode = displayMode;
            MainWindow.Log($"[{_roleName}] Starting capture with mode: {displayMode}, autoDetect: {enableFormatDetection}");
            try
            {
                _deckLinkInput.SetCallback(this);

                var flags = enableFormatDetection
                    ? _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection
                    : _BMDVideoInputFlags.bmdVideoInputFlagDefault;

                try
                {
                    _deckLinkInput.EnableVideoInput(displayMode, _BMDPixelFormat.bmdFormat8BitYUV, flags);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[{_roleName}] EnableVideoInput with flags {flags} failed: {ex.Message}. Falling back to default flag...");
                    _deckLinkInput.EnableVideoInput(displayMode, _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoInputFlags.bmdVideoInputFlagDefault);
                }

                _deckLinkInput.EnableAudioInput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType16bitInteger, 2);
                _deckLinkInput.StartStreams();
                _isCapturing = true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[{_roleName}] Capture failed: {ex.Message}");
            }
        }

        public void StopCapture()
        {
            if (!_isCapturing) return;
            _deckLinkInput.StopStreams();
            _deckLinkInput.DisableVideoInput();
            _deckLinkInput.DisableAudioInput();
            _isCapturing = false;
        }

        public void StartPlayback(_BMDDisplayMode displayMode = _BMDDisplayMode.bmdModeHD1080i50)
        {
            if (_isPlaying) return;

            MainWindow.Log($"[{_roleName}] Starting playback with mode: {displayMode}");
            try 
            {
                _deckLinkOutput.EnableVideoOutput(displayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
                _deckLinkOutput.EnableAudioOutput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType16bitInteger, 2, _BMDAudioOutputStreamType.bmdAudioOutputStreamContinuous);
                _deckLinkAudioTask = Task.Run(DeckLinkAudioOutputWorker);
                _systemAudioTask = Task.Run(SystemAudioOutputWorker);
                _isPlaying = true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[{_roleName}] Playback failed: {ex.Message}");
            }
        }

        public void StopPlayback()
        {
            if (!_isPlaying) return;
            _audioOutputQueue.CompleteAdding();
            _systemAudioQueue.CompleteAdding();
            try { _deckLinkAudioTask?.Wait(200); } catch { }
            try { _systemAudioTask?.Wait(200); } catch { }
            _deckLinkOutput.DisableVideoOutput();
            _deckLinkOutput.DisableAudioOutput();
            _isPlaying = false;
        }

        private void QueueAudioOutput(IntPtr audioBuffer, uint sampleCount)
        {
            if (audioBuffer == IntPtr.Zero || sampleCount == 0 || _audioOutputQueue.IsAddingCompleted) return;

            byte[] data = new byte[checked((int)sampleCount * 4)];
            System.Runtime.InteropServices.Marshal.Copy(audioBuffer, data, 0, data.Length);
            var packet = new PendingAudioPacket { Data = data, SampleCount = sampleCount };
            EnqueueLatest(_audioOutputQueue, packet, ref _deckLinkAudioDrops);
            EnqueueLatest(_systemAudioQueue, packet, ref _systemAudioDrops);

            long now = Environment.TickCount64;
            long next = Interlocked.Read(ref _nextOutputDiagnosticsTick);
            if (now >= next && Interlocked.CompareExchange(ref _nextOutputDiagnosticsTick, now + 2000, next) == next)
            {
                MainWindow.Log($"Output audio diagnostic decklinkDepth={_audioOutputQueue.Count}, decklinkDrops={Interlocked.Read(ref _deckLinkAudioDrops)}, monitorDepth={_systemAudioQueue.Count}, monitorDrops={Interlocked.Read(ref _systemAudioDrops)}, monitorBufferedMs={SystemAudioPlayer.BufferedMilliseconds}");
            }
        }

        private static void EnqueueLatest(System.Collections.Concurrent.BlockingCollection<PendingAudioPacket> queue, PendingAudioPacket packet, ref long dropCount)
        {
            while (!queue.TryAdd(packet))
            {
                if (queue.IsAddingCompleted) return;
                if (queue.TryTake(out _)) Interlocked.Increment(ref dropCount);
            }
        }

        private void DeckLinkAudioOutputWorker()
        {
            foreach (var packet in _audioOutputQueue.GetConsumingEnumerable())
            {
                IntPtr audioBuffer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(packet.Data.Length);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(packet.Data, 0, audioBuffer, packet.Data.Length);
                    try { uint written; _deckLinkOutput.WriteAudioSamplesSync(audioBuffer, packet.SampleCount, out written); } catch { }
                }
                finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(audioBuffer); }
            }
        }

        private void SystemAudioOutputWorker()
        {
            foreach (var packet in _systemAudioQueue.GetConsumingEnumerable())
            {
                SystemAudioPlayer.WriteAudio(packet.Data, packet.SampleCount);
            }
        }

        private IDeckLinkMutableVideoFrame _reusableOutputFrame;
        private sealed class PendingAudioPacket
        {
            public byte[] Data { get; init; }
            public uint SampleCount { get; init; }
        }

        private readonly System.Collections.Concurrent.BlockingCollection<PendingAudioPacket> _audioOutputQueue = new(4);
        private readonly System.Collections.Concurrent.BlockingCollection<PendingAudioPacket> _systemAudioQueue = new(4);
        private static long _nextOutputDiagnosticsTick;
        private long _deckLinkAudioDrops;
        private long _systemAudioDrops;
        private Task _deckLinkAudioTask;
        private Task _systemAudioTask;

        [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
        public static extern IntPtr memcpy(IntPtr dest, IntPtr src, UIntPtr count);

        private int _isDisplaying = 0;

        public void ScheduleFrame(IDeckLinkVideoInputFrame inputFrame, IDeckLinkAudioInputPacket audioPacket, float inputVolume = 1.0f)
        {
            int width = inputFrame.GetWidth();
            int height = inputFrame.GetHeight();
            int rowBytes = inputFrame.GetRowBytes();
            _BMDPixelFormat pixelFormat = inputFrame.GetPixelFormat();

            if (_reusableOutputFrame == null)
            {
                _deckLinkOutput.CreateVideoFrame(1920, 1080, 1920 * 2, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out _reusableOutputFrame);
            }

            uint audioSampleCount = audioPacket != null ? (uint)audioPacket.GetSampleFrameCount() : 1920;
            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName, "decklink-input-output");
            QueueAudioOutput(modifiedAudioBuffer, audioSampleCount);

            if (Interlocked.CompareExchange(ref _isDisplaying, 1, 0) == 1)
            {
                if (modifiedAudioBuffer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                return;
            }

            var inputBuf = (IDeckLinkVideoBuffer)inputFrame;
            var outputBuf = (IDeckLinkVideoBuffer)_reusableOutputFrame;

            inputBuf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
            try
            {
                outputBuf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite);
                try
                {
                    inputBuf.GetBytes(out IntPtr inputBuffer);
                    outputBuf.GetBytes(out IntPtr outputBuffer);

                    if (width == 1920 && height == 1080)
                    {
                        UIntPtr size = new UIntPtr(1920 * 1080 * 2);
                        memcpy(outputBuffer, inputBuffer, size);
                    }
                    else
                    {
                        unsafe
                        {
                            ScaleUyvyTo1080Uyvy((byte*)inputBuffer, width, height, rowBytes, (byte*)outputBuffer, 1920 * 2);
                        }
                    }
                    unsafe
                    {
                        MainWindow.ApplyLogoOverlayUyvy((byte*)outputBuffer);
                    }
                }
                finally
                {
                    outputBuf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite);
                }
            }
            finally
            {
                inputBuf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
            }

            System.Threading.Tasks.Task.Run(() => 
            {
                try 
                {
                    byte[] uyvyBytes = null;
                    if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording || MainWindow.NdiOut.IsRunning || MainWindow.SrtOut.IsStreaming)
                    {
                        int h = _reusableOutputFrame.GetHeight();
                        int outRowBytes = _reusableOutputFrame.GetRowBytes();
                        uyvyBytes = new byte[outRowBytes * h];
                        var buf = (IDeckLinkVideoBuffer)_reusableOutputFrame; 
                        buf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead); 
                        buf.GetBytes(out IntPtr uyvyPtr); 
                        System.Runtime.InteropServices.Marshal.Copy(uyvyPtr, uyvyBytes, 0, uyvyBytes.Length); 
                        buf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                        
                        // Check if it's completely black!
                        bool isBlack = true;
                        for(int i = 0; i < 1000 && i < uyvyBytes.Length; i++) { if (uyvyBytes[i] != 0) isBlack = false; }
                        if (isBlack) MainWindow.Log("WARNING: uyvyBytes is BLANK in Task.Run!");
                    }

                    try { _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame); } catch { }
                    if (modifiedAudioBuffer != IntPtr.Zero && audioSampleCount > 0)
                    {
                        MainWindow.OutputProgramAudioAndVideo(modifiedAudioBuffer, audioSampleCount, uyvyBytes, false);
                    }
                } 
                catch (Exception ex) 
                {
                    MainWindow.Log($"[{_roleName}] Task.Run DisplayVideo/Audio error: {ex.Message}");
                }
                finally
                {
                    if (modifiedAudioBuffer != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                    }
                    Interlocked.Exchange(ref _isDisplaying, 0);
                }
            });
        }
        
        public void ScheduleCustomFrame(IntPtr inputBuffer, int width, int height, int rowBytes, IntPtr audioBuffer, uint audioSampleCount, float inputVolume = 1.0f)
        {
            if (_reusableOutputFrame == null)
            {
                _deckLinkOutput.CreateVideoFrame(1920, 1080, 1920 * 2, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out _reusableOutputFrame);
            }

            uint mixedSampleCount = audioSampleCount > 0 ? audioSampleCount : 1920;
            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(mixedSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName, "media-frame-output");
            QueueAudioOutput(modifiedAudioBuffer, mixedSampleCount);

            if (Interlocked.CompareExchange(ref _isDisplaying, 1, 0) == 1)
            {
                if (modifiedAudioBuffer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                return;
            }

            var outputBuf = (IDeckLinkVideoBuffer)_reusableOutputFrame;
            outputBuf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite);
            try
            {
                outputBuf.GetBytes(out IntPtr outputBuffer);
                if (width == 1920 && height == 1080)
                {
                    UIntPtr size = new UIntPtr(1920 * 1080 * 2);
                    memcpy(outputBuffer, inputBuffer, size);
                }
                else
                {
                    unsafe
                    {
                        ScaleUyvyTo1080Uyvy((byte*)inputBuffer, width, height, rowBytes, (byte*)outputBuffer, 1920 * 2);
                    }
                }
                unsafe
                {
                    MainWindow.ApplyLogoOverlayUyvy((byte*)outputBuffer);
                }
            }
            finally
            {
                outputBuf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite);
            }

            System.Threading.Tasks.Task.Run(() => 
            {
                try 
                {
                    byte[] uyvyBytes = null;
                    if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording || MainWindow.NdiOut.IsRunning || MainWindow.SrtOut.IsStreaming)
                    {
                        int h = _reusableOutputFrame.GetHeight();
                        int rowBytes = _reusableOutputFrame.GetRowBytes();
                        uyvyBytes = new byte[rowBytes * h];
                        var buf = (IDeckLinkVideoBuffer)_reusableOutputFrame; 
                        buf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead); 
                        buf.GetBytes(out IntPtr uyvyPtr); 
                        System.Runtime.InteropServices.Marshal.Copy(uyvyPtr, uyvyBytes, 0, uyvyBytes.Length); 
                        buf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                        
                        // Check if it's completely black!
                        bool isBlack = true;
                        for(int i = 0; i < 1000 && i < uyvyBytes.Length; i++) { if (uyvyBytes[i] != 0) isBlack = false; }
                        if (isBlack) MainWindow.Log("WARNING: uyvyBytes is BLANK in Task.Run!");
                    }

                    try { _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame); } catch { }


                    if (modifiedAudioBuffer != IntPtr.Zero && mixedSampleCount > 0)
                    {
                        MainWindow.OutputProgramAudioAndVideo(modifiedAudioBuffer, mixedSampleCount, uyvyBytes, false);
                    }
                } 
                catch (Exception ex) 
                {
                    MainWindow.Log($"[{_roleName}] Task.Run DisplayCustomVideo/Audio error: {ex.Message}");
                }
                finally
                {
                    if (modifiedAudioBuffer != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                    }
                    Interlocked.Exchange(ref _isDisplaying, 0);
                }
            });
        }

        private double _audioPhase = 0;

        public void ScheduleSyntheticFrame(int sourceType)
        {
            if (_reusableOutputFrame == null)
            {
                _deckLinkOutput.CreateVideoFrame(1920, 1080, 1920 * 2, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out _reusableOutputFrame);
            }

            uint audioSampleCount = 1920;
            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName, "synthetic-frame-output");
            QueueAudioOutput(modifiedAudioBuffer, audioSampleCount);

            if (Interlocked.CompareExchange(ref _isDisplaying, 1, 0) == 1)
            {
                if (modifiedAudioBuffer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                return;
            }

            var outputBuf = (IDeckLinkVideoBuffer)_reusableOutputFrame;
            outputBuf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite);
            try
            {
                outputBuf.GetBytes(out IntPtr outputBuffer);
                int width = 1920;
                int height = 1080;
                int rowBytes = 1920 * 2;

                unsafe
                {
                    byte* ptr = (byte*)outputBuffer.ToPointer();
                    if (sourceType == 2) // Matte
                    {
                        // Matte Color (UYVY)
                        for (int i = 0; i < height * rowBytes; i += 4)
                        {
                            ptr[i] = MainWindow.MatteU; // U
                            ptr[i + 1] = MainWindow.MatteY; // Y
                            ptr[i + 2] = MainWindow.MatteV; // V
                            ptr[i + 3] = MainWindow.MatteY; // Y
                        }
                    }
                    else if (sourceType == 1) // Color Bars
                    {
                        // 8 color bars
                        byte[,] colors = new byte[8, 4] {
                            { 128, 235, 128, 235 }, // White
                            { 44, 210, 171, 210 }, // Yellow
                            { 156, 170, 44, 170 }, // Cyan
                            { 74, 145, 84, 145 }, // Green
                            { 182, 106, 172, 106 }, // Magenta
                            { 98, 81, 212, 81 }, // Red
                            { 212, 41, 114, 41 }, // Blue
                            { 128, 16, 128, 16 }  // Black
                        };

                        for (int y = 0; y < height; y++)
                        {
                            byte* rowPtr = ptr + y * rowBytes;
                            for (int x = 0; x < width; x += 2)
                            {
                                int barIndex = (x * 8) / width;
                                int offset = x * 2;
                                rowPtr[offset] = colors[barIndex, 0];
                                rowPtr[offset + 1] = colors[barIndex, 1];
                                rowPtr[offset + 2] = colors[barIndex, 2];
                                rowPtr[offset + 3] = colors[barIndex, 3];
                            }
                        }
                    }
                    else if (sourceType == 6) // YouTube direct 1080p UYVY
                    {
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow.LatestYouTubeUyvy != null)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(MainWindow.LatestYouTubeUyvy, 0, outputBuffer, MainWindow.LatestYouTubeUyvy.Length);
                            }
                        }
                    }
                    else if (sourceType == 8) // NDI source direct 1080p UYVY
                    {
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow.LatestNdiUyvy != null)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(MainWindow.LatestNdiUyvy, 0, outputBuffer, MainWindow.LatestNdiUyvy.Length);
                            }
                        }
                    }
                    else if (sourceType == 7) // HTML / Web direct 1080p UYVY
                    {
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow.LatestHtmlSourceUyvy != null)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(MainWindow.LatestHtmlSourceUyvy, 0, outputBuffer, MainWindow.LatestHtmlSourceUyvy.Length);
                            }
                        }
                    }
                    else if (sourceType == 4 || sourceType == 5) // Desktop Capture or Webcam
                    {
                        byte[] deskUyvy = null;
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow._latestPgmFrame != null)
                            {
                                if (MainWindow._latestPgmFrame.Length == 1920 * 1080 * 4)
                                    deskUyvy = MainWindow.ConvertBgra1080ToUyvy1080(MainWindow._latestPgmFrame);
                                else
                                    deskUyvy = MainWindow.ConvertBgraToUyvy1080(MainWindow._latestPgmFrame);
                            }
                        }
                        if (deskUyvy != null)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(deskUyvy, 0, outputBuffer, deskUyvy.Length);
                        }
                    }

                    MainWindow.ApplyLogoOverlayUyvy(ptr);
                }
            }
            finally
            {
                outputBuf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite);
            }

            // Audio is now generated in the background loop!
            try 
            {
                byte[] uyvyBytes = null;
                if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording || MainWindow.NdiOut.IsRunning || MainWindow.SrtOut.IsStreaming)
                {
                    int h = _reusableOutputFrame.GetHeight();
                    int rowBytes = _reusableOutputFrame.GetRowBytes();
                    uyvyBytes = new byte[rowBytes * h];
                    var buf = (IDeckLinkVideoBuffer)_reusableOutputFrame; 
                    buf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead); 
                    buf.GetBytes(out IntPtr uyvyPtr); 
                    System.Runtime.InteropServices.Marshal.Copy(uyvyPtr, uyvyBytes, 0, uyvyBytes.Length); 
                    buf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                }

                try { _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame); } catch { }
                if (modifiedAudioBuffer != IntPtr.Zero && audioSampleCount > 0)
                {
                    MainWindow.OutputProgramAudioAndVideo(modifiedAudioBuffer, audioSampleCount, uyvyBytes, false);
                }
            } 
            catch (Exception ex) 
            {
                MainWindow.Log($"[{_roleName}] DisplaySyntheticVideo/Audio error: {ex.Message}");
            }
            finally
            {
                if (modifiedAudioBuffer != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                }
                Interlocked.Exchange(ref _isDisplaying, 0);
            }
        }

        void IDeckLinkInputCallback.VideoInputFormatChanged(_BMDVideoInputFormatChangedEvents notificationEvents, IDeckLinkDisplayMode newDisplayMode, _BMDDetectedVideoInputFormatFlags detectedSignalFlags)
        {
            try
            {
                if (newDisplayMode == null) return;

                _BMDDisplayMode mode = newDisplayMode.GetDisplayMode();
                newDisplayMode.GetName(out string modeName);
                MainWindow.Log($"[{_roleName}] Video input format change detected: {modeName} ({mode})");

                if ((notificationEvents & _BMDVideoInputFormatChangedEvents.bmdVideoInputDisplayModeChanged) != 0 && mode != _currentDisplayMode)
                {
                    MainWindow.Log($"[{_roleName}] Renegotiating input stream to {modeName}...");
                    _deckLinkInput.PauseStreams();
                    _deckLinkInput.EnableVideoInput(mode, _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection);
                    _deckLinkInput.FlushStreams();
                    _deckLinkInput.StartStreams();
                    _currentDisplayMode = mode;
                    MainWindow.Log($"[{_roleName}] Successfully updated stream format to {modeName}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[{_roleName}] VideoInputFormatChanged error: {ex.Message}");
            }
        }

        void IDeckLinkInputCallback.VideoInputFrameArrived(IDeckLinkVideoInputFrame videoFrame, IDeckLinkAudioInputPacket audioPacket)
        {
            if (videoFrame != null)
            {
                if ((videoFrame.GetFlags() & _BMDFrameFlags.bmdFrameHasNoInputSource) == _BMDFrameFlags.bmdFrameHasNoInputSource)
                {
                    return; // No signal
                }

                _frameCount++;
                if (_frameCount % 60 == 0)
                {
                    MainWindow.Log($"[{_roleName}] Receiving valid video frames (Count: {_frameCount})...");
                }

                try 
                {
                    bool shouldUpdatePreview = false;
                    if (IsProgramSource) shouldUpdatePreview = true;
                    else if (PreviewBitmap != null && Environment.TickCount - _lastPreviewTicks > 200) shouldUpdatePreview = true;

                    if (shouldUpdatePreview)
                    {
                        _lastPreviewTicks = Environment.TickCount;
                        
                        var inputBuf = (IDeckLinkVideoBuffer)videoFrame;
                        int width = videoFrame.GetWidth();
                        int height = videoFrame.GetHeight();
                        int srcRowBytes = videoFrame.GetRowBytes();
                        
                        inputBuf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                        try
                        {
                            inputBuf.GetBytes(out IntPtr inputBuffer);
                            
                            unsafe 
                            {
                                fixed (byte* dest = _previewBuffer)
                                {
                                    ConvertUYVYToBGRA_Downsampled(inputBuffer, width, height, srcRowBytes, (IntPtr)dest, 480, 270, 480 * 4);
                                }
                            }
                            
                            byte[] bufferCopy = (byte[])_previewBuffer.Clone();
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                PreviewBitmap.WritePixels(new Int32Rect(0, 0, 480, 270), bufferCopy, 480 * 4, 0);
                                OnPreviewBufferUpdated?.Invoke(bufferCopy);
                            }));
                        }
                        finally
                        {
                            inputBuf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                        }
                    }

                    if (audioPacket != null)
                    {
                        audioPacket.GetBytes(out IntPtr audioPtr);
                        int sampleCount = (int)audioPacket.GetSampleFrameCount();
                        
                        short[] arr = new short[sampleCount * 2];
                        System.Runtime.InteropServices.Marshal.Copy(audioPtr, arr, 0, sampleCount * 2);
                        if (MainWindow.InvertDeckLinkRight)
                        {
                            for (int i = 1; i < arr.Length; i += 2)
                                arr[i] = arr[i] == short.MinValue ? short.MaxValue : (short)(-arr[i]);
                        }
                        MainWindow.PushAudioPacket(_roleName, arr);

                        if (OnAudioLevelArrived != null && Environment.TickCount - _lastPreviewTicks <= 200) 
                        {
                            unsafe 
                            {
                                short* samples = (short*)audioPtr.ToPointer();
                                int maxL = 0;
                                int maxR = 0;
                                for (int i = 0; i < sampleCount * 2; i += 2) // 2 channels
                                {
                                    int valL = Math.Abs((int)samples[i]);
                                    int valR = Math.Abs((int)samples[i+1]);
                                    if (valL > maxL) maxL = valL;
                                    if (valR > maxR) maxR = valR;
                                }
                                
                                int volL = (maxL * 100) / 32768;
                                int volR = (maxR * 100) / 32768;
                                OnAudioLevelArrived(volL, volR);
                            }
                        }
                    }

                    OnVideoAndAudioArrived?.Invoke(videoFrame, audioPacket);
                }  
                catch (Exception ex) 
                {
                    MainWindow.Log($"[{_roleName}] Error displaying frame: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        public static unsafe void ConvertUYVYToBGRA_Downsampled(IntPtr uyvyBuffer, int width, int height, int srcRowBytes, IntPtr bgraBuffer, int destWidth, int destHeight, int destRowBytes)
        {
            byte* src = (byte*)uyvyBuffer;
            byte* dest = (byte*)bgraBuffer;
            
            float scaleX = (float)width / destWidth;
            float scaleY = (float)height / destHeight;

            for (int y = 0; y < destHeight; y++)
            {
                int srcY = (int)(y * scaleY);
                byte* srcRow = src + srcY * srcRowBytes;
                byte* destRow = dest + y * destRowBytes;
                
                for (int x = 0; x < destWidth; x++)
                {
                    int srcX = (int)(x * scaleX);
                    int macroPixelOffset = (srcX / 2) * 4;
                    
                    byte u = srcRow[macroPixelOffset + 0];
                    byte y_val = srcRow[macroPixelOffset + (srcX % 2 == 0 ? 1 : 3)];
                    byte v = srcRow[macroPixelOffset + 2];
                    
                    int c = y_val - 16;
                    int d = u - 128;
                    int e = v - 128;

                    int r = (298 * c + 409 * e + 128) >> 8;
                    int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                    int b = (298 * c + 516 * d + 128) >> 8;

                    r = r < 0 ? 0 : (r > 255 ? 255 : r);
                    g = g < 0 ? 0 : (g > 255 ? 255 : g);
                    b = b < 0 ? 0 : (b > 255 ? 255 : b);

                    int destOffset = x * 4;
                    destRow[destOffset + 0] = (byte)b;
                    destRow[destOffset + 1] = (byte)g;
                    destRow[destOffset + 2] = (byte)r;
                    destRow[destOffset + 3] = 255;
                }
            }
        }
    }
    
    public class RecordingProfileDefinition
    {
        public string DisplayName { get; }
        public string ContainerExtension { get; }
        public string OutputOptions { get; }
        public string VideoFilter { get; }
        public string FileNameSuffix { get; }
        public bool UseDualMonoAudio { get; }

        public RecordingProfileDefinition(string displayName, string containerExtension, string outputOptions, string videoFilter = null, string fileNameSuffix = null, bool useDualMonoAudio = false)
        {
            DisplayName = displayName;
            ContainerExtension = containerExtension;
            OutputOptions = outputOptions;
            VideoFilter = videoFilter;
            FileNameSuffix = string.IsNullOrWhiteSpace(fileNameSuffix) ? displayName : fileNameSuffix;
            UseDualMonoAudio = useDualMonoAudio;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}





