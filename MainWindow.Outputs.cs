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
        private DateTime? _recordStartUtc;
        private DateTime? _streamStartUtc;
        private DateTime? _srtStartUtc;
        private DateTime? _ndiStartUtc;
        private System.Windows.Threading.DispatcherTimer? _telemetryTimer;
        private bool _ftbBlinkPhase = false;

        private void EnsureTelemetryTimer()
        {
            if (_telemetryTimer != null) return;
            _telemetryTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _telemetryTimer.Tick += TelemetryTimer_Tick;
            _telemetryTimer.Start();
        }

        private void TelemetryTimer_Tick(object? sender, EventArgs e)
        {
            if (LclRecorder.IsRecording)
            {
                _recordStartUtc ??= DateTime.UtcNow;
                var elapsed = DateTime.UtcNow - _recordStartUtc.Value;
                if (BtnRecord != null) BtnRecord.Content = $"■ STOP REC  {elapsed:hh\\:mm\\:ss}";
            }
            else if (_recordStartUtc.HasValue)
            {
                _recordStartUtc = null;
                ApplyOutputButtonStyle(BtnRecord, false, "STOP RECORDING", "START RECORDING");
                UpdateRecordingModeUiState();
            }

            if (YtStreamer.IsStreaming)
            {
                _streamStartUtc ??= DateTime.UtcNow;
                var elapsed = DateTime.UtcNow - _streamStartUtc.Value;
                if (BtnStream != null) BtnStream.Content = $"■ STOP STREAM  {elapsed:hh\\:mm\\:ss}";
            }
            else if (_streamStartUtc.HasValue)
            {
                _streamStartUtc = null;
                ApplyOutputButtonStyle(BtnStream, false, "STOP STREAM", "START STREAM");
            }

            if (SrtOut.IsStreaming)
            {
                _srtStartUtc ??= DateTime.UtcNow;
                var elapsed = DateTime.UtcNow - _srtStartUtc.Value;
                if (BtnSrt != null) BtnSrt.Content = $"■ STOP SRT  {elapsed:hh\\:mm\\:ss}";
            }
            else if (_srtStartUtc.HasValue)
            {
                _srtStartUtc = null;
                UpdateSrtUiState(false);
            }

            if (NdiOut.IsRunning)
            {
                _ndiStartUtc ??= DateTime.UtcNow;
                var elapsed = DateTime.UtcNow - _ndiStartUtc.Value;
                if (BtnNdi != null) BtnNdi.Content = $"■ STOP NDI  {elapsed:hh\\:mm\\:ss}";
            }
            else if (_ndiStartUtc.HasValue)
            {
                _ndiStartUtc = null;
                ApplyOutputButtonStyle(BtnNdi, false, "STOP NDI", "START NDI");
            }

            UpdateFtbButtonState(toggleBlink: true);
        }

        private void UpdateFtbButtonState(bool toggleBlink)
        {
            if (PgmFtbBlackOverlay != null)
            {
                PgmFtbBlackOverlay.Opacity = GetFtbLevel();
            }
            if (BtnFtb == null) return;
            if (IsFtbActive || _isFtbFading)
            {
                if (toggleBlink) _ftbBlinkPhase = !_ftbBlinkPhase;
                BtnFtb.Content = IsFtbActive ? "FTB (ON)" : "FTB";
                BtnFtb.Background = _ftbBlinkPhase
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(127, 0, 0));
                BtnFtb.Foreground = System.Windows.Media.Brushes.White;
            }
            else
            {
                _ftbBlinkPhase = false;
                BtnFtb.Content = "FTB";
                BtnFtb.Background = (System.Windows.Media.Brush)FindResource("ButtonAltBg");
                BtnFtb.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary");
            }
        }

        private void BtnFtb_Click(object sender, RoutedEventArgs e)
        {
            ToggleFtb();
            EnsureTelemetryTimer();
            _ftbBlinkPhase = true;
            UpdateFtbButtonState(toggleBlink: false);
            Log(IsFtbActive ? "Fade to Black (FTB) ON" : "Fade to Black (FTB) OFF");
        }

        private void BtnBrowseRecordingDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                TxtRecordingDirectory.Text = dialog.FolderName;
                SaveCurrentSettings();
            }
        }
        
        private bool IsIntervalRecordingModeSelected()
        {
            string mode = (CmbRecordingMode?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Infinite Record";
            return string.Equals(mode, "Interval Record", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateRecordingModeUiState()
        {
            bool isInterval = IsIntervalRecordingModeSelected();
            bool isRec = LclRecorder?.IsRecording == true;
            if (PnlRecordingInterval != null)
                PnlRecordingInterval.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
            if (CmbRecordingMode != null)
                CmbRecordingMode.IsEnabled = !isRec;
            if (TxtRecordingIntervalSec != null)
                TxtRecordingIntervalSec.IsEnabled = !isRec && isInterval;
        }

        private void CmbRecordingMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateRecordingModeUiState();
            SaveCurrentSettings();
        }

        private void TxtRecordingIntervalSec_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _isLoadingSettings) return;
            SaveCurrentSettings();
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (LclRecorder.IsRecording)
            {
                LclRecorder.Stop();
                _recordStartUtc = null;
                ApplyOutputButtonStyle(BtnRecord, false, "STOP RECORDING", "START RECORDING");
                UpdateRecordingModeUiState();
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

                    bool intervalMode = IsIntervalRecordingModeSelected();
                    int intervalSec = 60;
                    if (intervalMode)
                    {
                        if (!int.TryParse(TxtRecordingIntervalSec?.Text.Trim(), out intervalSec) || intervalSec < 1)
                        {
                            System.Windows.MessageBox.Show("Please enter a valid recording interval in seconds (1 or more).");
                            return;
                        }
                    }

                    SaveCurrentSettings();
                    UpdatePgmPreviewSynthetic();
                    LclRecorder.Start(profile, dir, 1920, 1080, 25, intervalMode, intervalSec);
                    _recordStartUtc = DateTime.UtcNow;
                    EnsureTelemetryTimer();
                    ApplyOutputButtonStyle(BtnRecord, true, "■ STOP REC  00:00:00", "START RECORDING");
                    UpdateRecordingModeUiState();
                    TxtStatus.Text = intervalMode ? $"Interval recording started ({intervalSec}s clips)..." : "Infinite recording started...";
                }
            }
        }
        
        private void BtnStream_Click(object sender, RoutedEventArgs e)
        {
            if (YtStreamer.IsStreaming)
            {
                YtStreamer.Stop();
                _streamStartUtc = null;
                ApplyOutputButtonStyle(BtnStream, false, "STOP STREAM", "START STREAM");
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
                
                _streamStartUtc = DateTime.UtcNow;
                EnsureTelemetryTimer();
                ApplyOutputButtonStyle(BtnStream, true, "■ STOP STREAM  00:00:00", "START STREAM");
                TxtStatus.Text = "Streaming started...";
                // Seed the latest PGM frame immediately (synchronous) so stream loop has data right away
                UpdatePgmPreviewSynthetic();
                YtStreamer.Start(TxtStreamKey.Text, 1920, 1080, 25);
                
            }
        }

        private void BtnStream2_Click(object sender, RoutedEventArgs e)
        {
            if (YtStreamer2.IsStreaming)
            {
                YtStreamer2.Stop();
                ApplyOutputButtonStyle(BtnStream2, false, "STOP STREAM 2", "START STREAM 2");
                TxtStatus.Text = "YouTube Stream 2 stopped.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(TxtStreamKey2.Text))
                {
                    System.Windows.MessageBox.Show("Please enter a YouTube Stream Key 2.");
                    return;
                }

                var settings = AppSettings.Load();
                settings.YouTubeStreamKey2 = TxtStreamKey2.Text.Trim();
                settings.Save();

                ApplyOutputButtonStyle(BtnStream2, true, "■ STOP STREAM 2", "START STREAM 2");
                TxtStatus.Text = "YouTube Stream 2 started...";
                UpdatePgmPreviewSynthetic();
                YtStreamer2.Start(TxtStreamKey2.Text.Trim(), 1920, 1080, 25);
            }
        }

        private void BtnFbStream_Click(object sender, RoutedEventArgs e)
        {
            if (FbStreamer.IsStreaming)
            {
                FbStreamer.Stop();
                ApplyOutputButtonStyle(BtnFbStream, false, "STOP FB STREAM", "START FB STREAM");
                TxtStatus.Text = "Facebook streaming stopped.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(TxtFbStreamKey.Text))
                {
                    System.Windows.MessageBox.Show("Please enter a Facebook Stream Key.");
                    return;
                }

                string fbServer = string.IsNullOrWhiteSpace(TxtFbServerUrl?.Text)
                    ? "rtmps://live-api-s.facebook.com:443/rtmp/"
                    : TxtFbServerUrl.Text.Trim();
                if (!fbServer.EndsWith("/")) fbServer += "/";

                var settings = AppSettings.Load();
                settings.FacebookServerUrl = fbServer;
                settings.FacebookStreamKey = TxtFbStreamKey.Text.Trim();
                settings.Save();

                ApplyOutputButtonStyle(BtnFbStream, true, "■ STOP FB STREAM", "START FB STREAM");
                TxtStatus.Text = "Facebook streaming started...";
                UpdatePgmPreviewSynthetic();
                FbStreamer.Start(TxtFbStreamKey.Text.Trim(), 1920, 1080, 25, fbServer);
            }
        }

        private void BtnNdi_Click(object sender, RoutedEventArgs e)
        {
            if (NdiOut.IsRunning)
            {
                NdiOut.Stop();
                _ndiStartUtc = null;
                ApplyOutputButtonStyle(BtnNdi, false, "STOP NDI", "START NDI");
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
                    _ndiStartUtc = DateTime.UtcNow;
                    EnsureTelemetryTimer();
                    ApplyOutputButtonStyle(BtnNdi, true, "■ STOP NDI  00:00:00", "START NDI");
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
                _srtStartUtc = null;
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
                    _srtStartUtc = DateTime.UtcNow;
                    EnsureTelemetryTimer();
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

        private void ApplyOutputButtonStyle(System.Windows.Controls.Button? btn, bool isActive, string activeText, string inactiveText)
        {
            if (btn == null) return;
            btn.Content = isActive ? activeText : inactiveText;
            btn.Background = isActive
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd3, 0x2f, 0x2f))
                : (System.Windows.Media.Brush)FindResource("SourceButtonBg");
            btn.Foreground = isActive
                ? System.Windows.Media.Brushes.White
                : (System.Windows.Media.Brush)FindResource("SourceButtonText");
        }

        private void UpdateSrtUiState(bool isStreaming)
        {
            ApplyOutputButtonStyle(BtnSrt, isStreaming, "STOP SRT", "START SRT");
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


        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F11)
            {
                e.Handled = true;
                ToggleFullscreenPreview();
            }
        }

        private void PgmPreviewGrid_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                ToggleFullscreenPreview();
            }
        }

        private void BtnFullscreenPreview_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreenPreview();
        }

        public void ToggleFullscreenPreview()
        {
            if (_fullscreenWindow != null && _fullscreenWindow.IsLoaded)
            {
                CloseFullscreenPreview();
                return;
            }

            OpenFullscreenPreview();
        }

        public void OpenFullscreenPreview()
        {
            if (_fullscreenWindow != null && _fullscreenWindow.IsLoaded)
            {
                _fullscreenWindow.Activate();
                return;
            }

            var screens = System.Windows.Forms.Screen.AllScreens;
            // Prefer secondary monitor; if none found, open on current/primary monitor
            var targetScreen = screens.FirstOrDefault(s => !s.Primary) ?? System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];

            var wnd = new FullscreenPreviewWindow();
            _fullscreenWindow = wnd;

            Interlocked.Exchange(ref _fullscreenFramePending, 0);
            _bmpFullscreenOutput = new System.Windows.Media.Imaging.WriteableBitmap(1920, 1080, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

            byte[]? seedUyvy = null;
            if (_activeSourceType == 9) seedUyvy = LatestCompositeUyvy;
            else if (_activeSourceType == 10) seedUyvy = LatestExtraSourceUyvy;
            else if (_activeSourceType == 8) seedUyvy = LatestNdiUyvy;
            else if (_activeSourceType == 7) seedUyvy = LatestHtmlSourceUyvy;
            else if (_activeSourceType == 6) seedUyvy = LatestYouTubeUyvy;
            else if (_activeSourceType == 1 || _activeSourceType == 2) seedUyvy = BuildSyntheticUyvy1080(_activeSourceType);

            if (seedUyvy != null && seedUyvy.Length == 1920 * 1080 * 2)
            {
                byte[] seedCopy;
                lock (_pgmLock) { seedCopy = (byte[])seedUyvy.Clone(); }
                if (_fullscreenBgraBuffer == null || _fullscreenBgraBuffer.Length != 1920 * 1080 * 4)
                    _fullscreenBgraBuffer = new byte[1920 * 1080 * 4];
                ConvertUyvy1080ToBgra1080(seedCopy, _fullscreenBgraBuffer);
                _bmpFullscreenOutput.WritePixels(new Int32Rect(0, 0, 1920, 1080), _fullscreenBgraBuffer, 1920 * 4, 0);
            }
            else if ((_activeSourceType == 0 || _activeSourceType == 3 || _activeSourceType == 4 || _activeSourceType == 5) &&
                     _latestPgmFrame != null && _latestPgmFrame.Length == 1920 * 1080 * 4)
            {
                _bmpFullscreenOutput.WritePixels(new Int32Rect(0, 0, 1920, 1080), _latestPgmFrame, 1920 * 4, 0);
            }
            IsFullscreenPreviewActive = true;

            // Connect 1920x1080 composited output (already includes NDI, HTML, and Logo 1080p overlays)
            wnd.FsPreviewOutput.Source = _bmpFullscreenOutput;

            wnd.Closed += (s, e) =>
            {
                if (ReferenceEquals(_fullscreenWindow, wnd))
                {
                    _fullscreenWindow = null;
                    IsFullscreenPreviewActive = false;
                    _bmpFullscreenOutput = null;
                }
                UpdateFullscreenButton();
            };

            // Position window on target display using DPI scale
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            wnd.WindowStartupLocation = WindowStartupLocation.Manual;
            wnd.Left = (targetScreen.Bounds.Left + 50) / dpiX;
            wnd.Top = (targetScreen.Bounds.Top + 50) / dpiY;
            wnd.Width = Math.Max(480, (targetScreen.Bounds.Width - 100) / dpiX);
            wnd.Height = Math.Max(270, (targetScreen.Bounds.Height - 100) / dpiY);

            wnd.Show();
            wnd.WindowState = WindowState.Maximized;

            UpdateFullscreenButton();
            Log($"Fullscreen preview (1920x1080) opened on {targetScreen.DeviceName} ({targetScreen.Bounds.Width}x{targetScreen.Bounds.Height}).");
        }

        public void CloseFullscreenPreview()
        {
            var wnd = _fullscreenWindow;
            _fullscreenWindow = null;
            IsFullscreenPreviewActive = false;
            _bmpFullscreenOutput = null;
            if (wnd != null && wnd.IsLoaded)
            {
                wnd.Close();
            }
            UpdateFullscreenButton();
        }

        private void UpdateFullscreenButton()
        {
            if (BtnFullscreenPreview != null)
            {
                bool isOpen = _fullscreenWindow != null && _fullscreenWindow.IsLoaded;
                BtnFullscreenPreview.Content = isOpen ? "✕ Exit Fullscreen" : "⛶ Fullscreen Preview";
                BtnFullscreenPreview.Background = isOpen
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40))
                    : (System.Windows.Media.Brush)FindResource("ButtonAltBg");
            }
        }


        public static void OutputProgramAudioAndVideo(IntPtr modifiedAudioBuffer, uint audioSampleCount, byte[] uyvyBytes, bool writeSystemAudio = true)
        {
            if (modifiedAudioBuffer != IntPtr.Zero && audioSampleCount > 0)
            {
            if (writeSystemAudio) SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, audioSampleCount);
                if (YtStreamer.IsStreaming || YtStreamer2.IsStreaming || FbStreamer.IsStreaming || LclRecorder.IsRecording || NdiOut.IsRunning || SrtOut.IsStreaming)
                {
                    byte[] audBytes = new byte[audioSampleCount * 4];
                    System.Runtime.InteropServices.Marshal.Copy(modifiedAudioBuffer, audBytes, 0, audBytes.Length);
                    if (YtStreamer.IsStreaming) YtStreamer.PushFrame(audBytes, uyvyBytes);
                    if (YtStreamer2.IsStreaming) YtStreamer2.PushFrame(audBytes, uyvyBytes);
                    if (FbStreamer.IsStreaming) FbStreamer.PushFrame(audBytes, uyvyBytes);
                    if (LclRecorder.IsRecording) LclRecorder.PushFrame(audBytes, uyvyBytes);
                    if (NdiOut.IsRunning) NdiOut.PushFrame(audBytes, uyvyBytes);
                    if (SrtOut.IsStreaming) SrtOut.PushFrame(audBytes, uyvyBytes);
                }
            }

            PushFullscreenPreviewFrame(uyvyBytes);
        }

        public static void PushFullscreenPreviewFrame(byte[]? uyvyBytes)
        {
            if (IsFullscreenPreviewActive && uyvyBytes != null && uyvyBytes.Length == 1920 * 1080 * 2 && _bmpFullscreenOutput != null)
            {
                if (Interlocked.CompareExchange(ref _fullscreenFramePending, 1, 0) == 0)
                {
                    try
                    {
                        if (_fullscreenBgraBuffer == null || _fullscreenBgraBuffer.Length != 1920 * 1080 * 4)
                            _fullscreenBgraBuffer = new byte[1920 * 1080 * 4];

                        ConvertUyvy1080ToBgra1080(uyvyBytes, _fullscreenBgraBuffer);

                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                _bmpFullscreenOutput?.WritePixels(new Int32Rect(0, 0, 1920, 1080), _fullscreenBgraBuffer, 1920 * 4, 0);
                            }
                            catch { }
                            finally
                            {
                                Interlocked.Exchange(ref _fullscreenFramePending, 0);
                            }
                        }), System.Windows.Threading.DispatcherPriority.Render);
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _fullscreenFramePending, 0);
                    }
                }
            }
        }

    }
}
