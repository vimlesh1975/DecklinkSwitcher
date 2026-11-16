using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
            if (window._activeSourceType != type || window._activeInput != device)
            {
                BeginMixIfEnabled();
            }
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
        private FullscreenPreviewWindow? _fullscreenWindow;
        public static volatile bool IsFullscreenPreviewActive = false;
        private static System.Windows.Media.Imaging.WriteableBitmap? _bmpFullscreenOutput;
        private static byte[]? _fullscreenBgraBuffer;
        private static int _fullscreenFramePending = 0;
        
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
        private System.Drawing.Bitmap[]? _rawLogoSequence = null;
        private System.Windows.Media.Imaging.BitmapSource? _logoPreviewImageSource = null;
        private System.Windows.Media.Imaging.BitmapSource[]? _logoPreviewSequence = null;
        private System.Windows.Threading.DispatcherTimer? _logoPreviewTimer = null;
        private bool _isUpdatingLogoUi = false;
        private bool _isLoadingSettings = true;

        public static volatile bool IsLogoOverlayEnabled = false;
        internal static volatile PreparedLogoOverlay? CurrentLogoOverlay = null;
        internal static volatile PreparedLogoOverlay[]? CurrentLogoSequence = null;
        public static volatile int LogoSequenceFps = 25;
        internal static long LogoSequenceStartMs = Environment.TickCount64;
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
                                if (YtStreamer.IsStreaming || LclRecorder.IsRecording || NdiOut.IsRunning || SrtOut.IsStreaming || IsFullscreenPreviewActive || IsMixEnabled)
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
                                    PushFullscreenPreviewFrame(uyvyBytes);
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
                    if (ChkMixTransition != null) settings.MixTransitionEnabled = ChkMixTransition.IsChecked == true;
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
                    if (CmbRecordingMode != null)
                        settings.RecordingMode = (CmbRecordingMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Infinite Record";
                    if (TxtRecordingIntervalSec != null && int.TryParse(TxtRecordingIntervalSec.Text.Trim(), out int recInt) && recInt > 0)
                        settings.RecordingIntervalSeconds = recInt;

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
                    settings.LogoSequenceFps = LogoSequenceFps;
                    settings.LastMediaFile = _lastMediaFile ?? "";
                    if (ChkLoopMedia != null) settings.MediaLoop = ChkLoopMedia.IsChecked == true;

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
            
            CloseFullscreenPreview();

            // Save Settings
            SaveCurrentSettings();

            _seekTimer?.Stop();
            _logoPreviewTimer?.Stop();

            // Hide the window immediately so it doesn't freeze on screen
            this.Hide();
            e.Cancel = true;

            // Run the cleanup in the background, then fully exit
            Task.Run(() =>
            {
                try { if (YtStreamer.IsStreaming) YtStreamer.Stop(); } catch { }
                try { if (LclRecorder.IsRecording) LclRecorder.Stop(); } catch { }
                try { if (NdiOut.IsRunning) NdiOut.Stop(); } catch { }
                try { if (SrtOut.IsStreaming) SrtOut.Stop(); } catch { }
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
                HtmlBrowserSource.ShutdownCef();
                _systemAudioSource?.Dispose();
                SystemAudioPlayer.Shutdown();
                try { timeEndPeriod(1); } catch { }
                Log("Resources released.");
                Environment.Exit(0);
            });
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                try { timeBeginPeriod(1); } catch { }
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
                IsMixEnabled = settings.MixTransitionEnabled;
                if (ChkMixTransition != null) ChkMixTransition.IsChecked = settings.MixTransitionEnabled;

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

                if (TxtRecordingIntervalSec != null)
                {
                    TxtRecordingIntervalSec.Text = Math.Max(1, settings.RecordingIntervalSeconds > 0 ? settings.RecordingIntervalSeconds : 60).ToString();
                }
                if (CmbRecordingMode != null)
                {
                    string modeText = string.IsNullOrWhiteSpace(settings.RecordingMode) ? "Infinite Record" : settings.RecordingMode;
                    foreach (ComboBoxItem item in CmbRecordingMode.Items)
                    {
                        if (string.Equals(item.Content?.ToString(), modeText, StringComparison.OrdinalIgnoreCase))
                        {
                            CmbRecordingMode.SelectedItem = item;
                            break;
                        }
                    }
                }
                UpdateRecordingModeUiState();

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

                    LogoSequenceFps = Math.Clamp(settings.LogoSequenceFps > 0 ? settings.LogoSequenceFps : 25, 1, 120);
                    if (CmbLogoSeqFps != null)
                    {
                        string fpsText = LogoSequenceFps.ToString();
                        foreach (ComboBoxItem item in CmbLogoSeqFps.Items)
                        {
                            if (string.Equals(item.Content?.ToString(), fpsText, StringComparison.Ordinal))
                            {
                                CmbLogoSeqFps.SelectedItem = item;
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(settings.LogoFilePath) && (File.Exists(settings.LogoFilePath) || Directory.Exists(settings.LogoFilePath)))
                    {
                        LoadLogoFromFile(settings.LogoFilePath);
                    }

                    SetLogoOverlayActiveState(settings.LogoOverlayEnabled && CurrentLogoOverlay != null);

                    if (ChkLoopMedia != null) ChkLoopMedia.IsChecked = settings.MediaLoop;
                    if (!string.IsNullOrWhiteSpace(settings.LastMediaFile) && File.Exists(settings.LastMediaFile))
                    {
                        _lastMediaFile = settings.LastMediaFile;
                        _mediaSource?.Cue(_lastMediaFile, settings.MediaLoop);
                    }

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

        private void ChkMixTransition_Changed(object sender, RoutedEventArgs e)
        {
            IsMixEnabled = ChkMixTransition.IsChecked == true;
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


        protected override void OnClosed(EventArgs e)
        {
            try { NdiOut.Stop(); } catch { }
            try { SrtOut.Stop(); } catch { }
            base.OnClosed(e);
        }
    }
}
