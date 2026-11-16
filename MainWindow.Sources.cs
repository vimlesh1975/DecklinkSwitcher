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

            ApplyOutputButtonStyle(BtnRecord, LclRecorder?.IsRecording == true, "STOP RECORDING", "START RECORDING");
            ApplyOutputButtonStyle(BtnStream, YtStreamer?.IsStreaming == true, "STOP STREAM", "START STREAM");
            ApplyOutputButtonStyle(BtnNdi, NdiOut?.IsRunning == true, "STOP NDI", "START NDI");
            ApplyOutputButtonStyle(BtnSrt, SrtOut?.IsStreaming == true, "STOP SRT", "START SRT");

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
            if (_activeSourceType != 1) BeginMixIfEnabled();
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
            if (_activeSourceType != 2) BeginMixIfEnabled();
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
            if (_activeSourceType != 3) BeginMixIfEnabled();
            _activeSourceType = 3; ActiveSourceType = 3; ActiveInputName = "Media"; Log("Switched to Local Video");
            _activeScreen = null; _activeWebcam = null; _activeInput = null;
            UpdateProgramSourceFlags();
        }
        private void PreviewMedia_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnMedia_Click(null, null);

        private void BtnYouTube_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSourceType != 6) BeginMixIfEnabled();
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
            if (_activeSourceType != 8) BeginMixIfEnabled();
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
            if (_activeSourceType != 7) BeginMixIfEnabled();
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
            if (_activeSourceType != 4 || _activeScreen != vm) BeginMixIfEnabled();
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
            if (_activeSourceType != 5 || _activeWebcam != vm) BeginMixIfEnabled();
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
                    SaveCurrentSettings();
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



        internal static unsafe void ConvertUyvy1080ToBgra1080(byte[] uyvy, byte[] bgra)
        {
            const int width = 1920;
            const int height = 1080;
            fixed (byte* srcBase = uyvy)
            fixed (byte* dstBase = bgra)
            {
                byte* pSrcBase = srcBase;
                byte* pDstBase = dstBase;
                Parallel.For(0, height, y =>
                {
                    byte* src = pSrcBase + y * width * 2;
                    byte* dst = pDstBase + y * width * 4;
                    for (int x = 0; x < width; x += 2)
                    {
                        int u = src[0] - 128;
                        int y0 = src[1] - 16;
                        int v = src[2] - 128;
                        int y1 = src[3] - 16;

                        int c0 = 298 * y0;
                        int c1 = 298 * y1;
                        int rDelta = 409 * v + 128;
                        int gDelta = -100 * u - 208 * v + 128;
                        int bDelta = 516 * u + 128;

                        int r0 = (c0 + rDelta) >> 8;
                        int g0 = (c0 + gDelta) >> 8;
                        int b0 = (c0 + bDelta) >> 8;

                        int r1 = (c1 + rDelta) >> 8;
                        int g1 = (c1 + gDelta) >> 8;
                        int b1 = (c1 + bDelta) >> 8;

                        dst[0] = (byte)(b0 < 0 ? 0 : (b0 > 255 ? 255 : b0));
                        dst[1] = (byte)(g0 < 0 ? 0 : (g0 > 255 ? 255 : g0));
                        dst[2] = (byte)(r0 < 0 ? 0 : (r0 > 255 ? 255 : r0));
                        dst[3] = 255;

                        dst[4] = (byte)(b1 < 0 ? 0 : (b1 > 255 ? 255 : b1));
                        dst[5] = (byte)(g1 < 0 ? 0 : (g1 > 255 ? 255 : g1));
                        dst[6] = (byte)(r1 < 0 ? 0 : (r1 > 255 ? 255 : r1));
                        dst[7] = 255;

                        src += 4;
                        dst += 8;
                    }
                });
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

        internal static byte[] ConvertBgra1080ToUyvy1080(byte[] bgra)
        {
            byte[] uyvy = new byte[1080 * 1920 * 2];
            ConvertBgra1080ToUyvy1080(bgra, uyvy);
            return uyvy;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static unsafe void ConvertBgra1080ToUyvy1080(byte[] bgra, byte[] uyvy)
        {
            const int dstW = 1920, dstH = 1080;
            fixed (byte* pBgra = bgra)
            fixed (byte* pUyvy = uyvy)
            {
                IntPtr ptrBgra = (IntPtr)pBgra;
                IntPtr ptrUyvy = (IntPtr)pUyvy;
                Parallel.For(0, dstH, y =>
                {
                    uint* srcRow32 = (uint*)((byte*)ptrBgra + y * (dstW * 4));
                    uint* dstRow32 = (uint*)((byte*)ptrUyvy + y * (dstW * 2));
                    int pairs = dstW >> 1;
                    for (int p = 0; p < pairs; p++)
                    {
                        uint px1 = srcRow32[p * 2];
                        uint px2 = srcRow32[p * 2 + 1];

                        int b1 = (int)(px1 & 0xFF);
                        int g1 = (int)((px1 >> 8) & 0xFF);
                        int r1 = (int)((px1 >> 16) & 0xFF);

                        int b2 = (int)(px2 & 0xFF);
                        int g2 = (int)((px2 >> 8) & 0xFF);
                        int r2 = (int)((px2 >> 16) & 0xFF);

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

                        dstRow32[p] = (uint)(U1 | (Y1 << 8) | (V1 << 16) | (Y2 << 24));
                    }
                });
            }
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
            if (YtStreamer.IsStreaming || LclRecorder.IsRecording || NdiOut.IsRunning || SrtOut.IsStreaming || IsFullscreenPreviewActive || IsMixEnabled)
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
                
                if (_activeInput == null)
                {
                    _activeInput = inputDev;
                    if (_activeSourceType == 0)
                    {
                        ActiveInputName = roleName;
                        Application.Current.Dispatcher.BeginInvoke(new Action(UpdateProgramSourceFlags));
                    }
                }
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

    }
}
