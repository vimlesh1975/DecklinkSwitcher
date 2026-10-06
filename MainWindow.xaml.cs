using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using DeckLinkAPI;

namespace DecklinkSwitcher
{
    public partial class MainWindow : Window
    {
        public static System.Collections.ObjectModel.ObservableCollection<DeckLinkInputViewModel> DynamicInputs { get; } = new();
        public System.Collections.ObjectModel.ObservableCollection<InputSelectionViewModel> InputSelectors { get; } = new();

        public static void SetActiveSource(int type, string name, DeckLinkDevice device)
        {
            var window = (MainWindow)Application.Current.MainWindow;
            window._activeSourceType = type;
            ActiveSourceType = type;
            window._activeInput = device;
            ActiveInputName = name;
            window.UpdateProgramSourceFlags();
            
            if (type == 0)
            {
                foreach (var input in DynamicInputs)
                {
                    input.BgColor = (input.Device == device && device != null) ? "#d32f2f" : "#2962ff";
                }
            }
            
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
        
        private VlcMediaSource _mediaSource;
        private System.Collections.Generic.List<LocalAudioInput> _activeMics = new();
        private CancellationTokenSource? _syntheticCts;
        public static YouTubeStreamer YtStreamer = new YouTubeStreamer();
        public static LocalRecorder LclRecorder = new LocalRecorder();
        
        // Latest PGM frame (BGRA 480x270) — updated by all PGM sources, read by streaming loop
        private static byte[]? _latestPgmFrame = null;
        private static readonly object _pgmLock = new object();
        private static CancellationTokenSource? _streamLoopCts;


        private static string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "decklinkswitcher_log.txt");

        private System.Windows.Threading.DispatcherTimer _seekTimer;
        private bool _isDraggingSeek = false;
        private TimeSpan _lastTotalProcessorTime;
        private DateTime _lastCpuTime;

        public MainWindow()
        {
            Log("=========================================");
            Log("Application Started");
            try
            {
                InitializeComponent();
                ((App)Application.Current).SetTheme(true);
                _lastTotalProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
                _lastCpuTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Log("InitializeComponent error: " + ex.ToString());
            }
            
            _bmpColorBars = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpMatte = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpMedia = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            PreviewColorBars.Source = _bmpColorBars;
            PreviewMatte.Source = _bmpMatte;
            PreviewMedia.Source = _bmpMedia;
            
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
                                if (YtStreamer.IsStreaming || LclRecorder.IsRecording)
                                {
                                    byte[] audBytes = new byte[mixedSampleCount * 4];
                                    System.Runtime.InteropServices.Marshal.Copy(modifiedAudioBuffer, audBytes, 0, audBytes.Length);
                                    if (YtStreamer.IsStreaming) YtStreamer.PushFrame(audBytes, null);
                                    if (LclRecorder.IsRecording) LclRecorder.PushFrame(audBytes, null);
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

            SystemAudioPlayer.Init();

            _seekTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _seekTimer.Tick += SeekTimer_Tick;
            _seekTimer.Start();

            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        public void SaveCurrentSettings()
        {
            try
            {
                var settings = new AppSettings();
                
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
                    }
                    
                    settings.AudioLevelPgm = SldPgmAudio.Value;
                    settings.AudioLevelColorBars = SldColorBarsAudio.Value;
                    settings.AudioLevelMedia = SldMediaAudio.Value;
                    settings.MatteColorIndex = CmbMatteColor.SelectedIndex;
                    settings.SystemAudioMonitorEnabled = ChkSystemAudioOut.IsChecked == true;
                    settings.LoopMediaEnabled = ChkLoopMedia.IsChecked == true;
                    
                    settings.AudioStateColorBars = (AudioState)CmbAudioStateColorBars.SelectedIndex;
                    settings.AudioStateMedia = (AudioState)CmbAudioStateMedia.SelectedIndex;
                    
                    foreach (var input in DynamicInputs)
                    {
                        settings.InputAudioLevels[input.DisplayName] = input.AudioLevel;
                        settings.InputAudioStates[input.DisplayName] = input.State;
                    }
                    
                    settings.MicLevels.Clear();
                    foreach (var kvp in DynamicMicLevels) settings.MicLevels[kvp.Key] = kvp.Value;
                    settings.MicStates.Clear();
                    settings.MicStates.Clear();
                    foreach (var kvp in DynamicMicStates) settings.MicStates[kvp.Key] = kvp.Value;
                    
                    settings.YouTubeStreamKey = TxtStreamKey.Text;
                    settings.SelectedRecordingProfile = (CmbRecordingProfile.SelectedItem as RecordingProfileDefinition)?.DisplayName ?? "MP4 High Quality";
                    settings.RecordingDirectory = TxtRecordingDirectory.Text;
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
                _syntheticCts?.Cancel();
                foreach (var input in DynamicInputs)
                {
                    if (input.Device != null) input.Device.StopCapture();
                }
                if (_activeOutput != null) _activeOutput.StopPlayback();
                if (_mediaSource != null) _mediaSource.Dispose();
                SystemAudioPlayer.Shutdown();
                Log("Resources released.");
                Environment.Exit(0);
            });
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("Window loaded. Discovering DeckLink devices...");
                
                UpdateMiniPreviewsSynthetic();
                
                List<DeckLinkDeviceInfo> devices = new List<DeckLinkDeviceInfo>();
                devices.Add(new DeckLinkDeviceInfo { DisplayName = "None", Index = -1 });

                Log("Creating DeckLinkIterator...");
                IDeckLinkIterator deckLinkIterator = null;
                try
                {
                    deckLinkIterator = new CDeckLinkIterator();
                    Log("DeckLinkIterator created successfully.");
                }
                catch (Exception ex)
                {
                    Log("Failed to create DeckLinkIterator: " + ex.ToString());
                }

                int physicalCount = 0;
                if (deckLinkIterator != null)
                {
                    while (true)
                    {
                        try
                        {
                            deckLinkIterator.Next(out IDeckLink deckLink);
                            if (deckLink == null) break;

                            deckLink.GetModelName(out string modelName);
                            // Differentiate Duo ports by adding a unique identifier or just listing them
                            // Since the API returns multiple identical "DeckLink Duo 2" names, we can append an index
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
                
                InputSelectorsControl.ItemsSource = InputSelectors;
                InputSelectors.Clear();
                int numInputs = Math.Max(0, physicalCount - 1);
                if (numInputs == 0) numInputs = 1; // Default to at least 1 selector if there are cards
                
                var availableDevices = new System.Collections.ObjectModel.ObservableCollection<DeckLinkDeviceInfo>(devices);
                for (int i = 0; i < numInputs; i++)
                {
                    string label = "Input " + (i + 1);
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
                

                _activeMics.Clear();
                var micDevices = LocalAudioInput.GetDevices();
                foreach (var device in micDevices)
                {
                    if (device.DeviceNumber < 0) continue; // Skip 'None'
                    string key = "Mic_" + device.DeviceNumber;
                    
                    double level = 1.0;
                    if (settings.MicLevels.ContainsKey(key)) level = settings.MicLevels[key];
                    AudioState state = AudioState.ON;
                    if (settings.MicStates.ContainsKey(key)) state = settings.MicStates[key];
                    
                    DynamicMicLevels[key] = (float)level;
                    DynamicMicStates[key] = state;
                    
                    var pnl = CreateMicPanel(device, key, level, state);
                    var wrapper = new System.Windows.Controls.Border();
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
                
                // Initialize recording profiles
                var profiles = new[]
                {
                    new RecordingProfileDefinition("XDCAM HD422", ".mxf", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a pcm_s16le -ar 48000 -ac 2", null, "XDCAM_HD422"),
                    new RecordingProfileDefinition("MP4 High Quality", ".mp4", "-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -profile:v high -movflags +faststart -c:a aac -b:a 192k -ar 48000 -ac 2", "bwdif=mode=send_frame:parity=auto:deint=all,scale=1920:1080:flags=lanczos,fps=25", "MP4_High"),
                    new RecordingProfileDefinition("MP4 Low Bitrate", ".mp4", "-c:v libx264 -preset veryfast -crf 24 -pix_fmt yuv420p -profile:v high -movflags +faststart -c:a aac -b:a 128k -ar 48000 -ac 2", "bwdif=mode=send_frame:parity=auto:deint=all,scale=1920:1080:flags=lanczos,fps=25", "MP4_Low"),
                    new RecordingProfileDefinition("TS H.264 High Quality", ".ts", "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -profile:v high -c:a aac -b:a 192k -ar 48000 -ac 2", "bwdif=mode=send_frame:parity=auto:deint=all,scale=1920:1080:flags=lanczos,fps=25", "TS_H264_High"),
                    new RecordingProfileDefinition("TS H.264 Low Bitrate", ".ts", "-c:v libx264 -preset veryfast -crf 25 -pix_fmt yuv420p -profile:v high -c:a aac -b:a 128k -ar 48000 -ac 2", "bwdif=mode=send_frame:parity=auto:deint=all,scale=1920:1080:flags=lanczos,fps=25", "TS_H264_Low"),
                    new RecordingProfileDefinition("TS MPEG-2 4:2:2 50M", ".ts", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a mp2 -b:a 384k -ar 48000 -ac 2", null, "TS_MPEG2_50M"),
                    new RecordingProfileDefinition("ProRes Proxy (Small)", ".mov", "-c:v prores_ks -profile:v 0 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 400 -c:a pcm_s16le -ar 48000", null, "ProRes_Proxy"),
                    new RecordingProfileDefinition("ProRes LT (Light)", ".mov", "-c:v prores_ks -profile:v 1 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1000 -c:a pcm_s16le -ar 48000", null, "ProRes_LT"),
                    new RecordingProfileDefinition("ProRes 422 (Medium)", ".mov", "-c:v prores_ks -profile:v 2 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1600 -c:a pcm_s16le -ar 48000", null, "ProRes_422"),
                    new RecordingProfileDefinition("ProRes 422 HQ (High)", ".mov", "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 2400 -c:a pcm_s16le -ar 48000", null, "ProRes_422_HQ"),
                    new RecordingProfileDefinition("MP4 4K H.264 (NVENC)", ".mp4", "-c:v h264_nvenc -preset p4 -cq 22 -pix_fmt yuv420p -r 25 -movflags +faststart -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_H264_NVENC"),
                    new RecordingProfileDefinition("MP4 4K HEVC (NVENC)", ".mp4", "-c:v hevc_nvenc -preset p4 -cq 24 -pix_fmt yuv420p -r 25 -movflags +faststart -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_HEVC_NVENC"),
                    new RecordingProfileDefinition("MP4 4K H.264 (CPU)", ".mp4", "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -profile:v high -r 25 -movflags +faststart -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_H264_CPU"),
                    new RecordingProfileDefinition("MP4 4K HEVC (CPU)", ".mp4", "-c:v libx265 -preset veryfast -crf 24 -pix_fmt yuv420p -r 25 -movflags +faststart -c:a aac -b:a 256k -ar 48000 -ac 2", null, "4K_HEVC_CPU"),
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
                    if (p.DisplayName == settings.SelectedRecordingProfile)
                    {
                        CmbRecordingProfile.SelectedItem = p;
                    }
                }
                if (CmbRecordingProfile.SelectedIndex == -1 && CmbRecordingProfile.Items.Count > 0)
                {
                    CmbRecordingProfile.SelectedIndex = 0;
                }
                
                TxtRecordingDirectory.Text = settings.RecordingDirectory;
                
                TxtStatus.Text = "Ready to assign.";
                Log("MainWindow_Loaded completed successfully.");
            }
            catch (Exception ex)
            {
                Log("MainWindow_Loaded error: " + ex.ToString());
            }
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
            var outInfo = CmbOutput.SelectedItem as DeckLinkDeviceInfo;


            if (outInfo == null)
            {
                TxtStatus.Text = "Please select an Output option (or None).";
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
            
            var selectedInputs = new System.Collections.Generic.List<DeckLinkDeviceInfo>();
            foreach (var selector in InputSelectors)
            {
                selectedInputs.Add(selector.SelectedDevice);
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
            foreach (var input in DynamicInputs)
            {
                if (input.Device != null) input.Device.IsProgramSource = (_activeSourceType == 0 && _activeInput == input.Device);
            }
        }

        private void BtnColorBars_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 1; ActiveSourceType = 1; ActiveInputName = "Color Bars";
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
            UpdateProgramSourceFlags();
        }
        private void PreviewMedia_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnMedia_Click(null, null);

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

        private void SeekTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                var currentCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
                var now = DateTime.UtcNow;
                var elapsedCpu = (currentCpuTime - _lastTotalProcessorTime).TotalMilliseconds;
                var elapsedWall = (now - _lastCpuTime).TotalMilliseconds;
                
                if (elapsedWall > 0)
                {
                    double cpu = elapsedCpu / (Environment.ProcessorCount * elapsedWall) * 100;
                    TxtCpuUsage.Text = $"{(int)cpu}%";
                }
                
                _lastTotalProcessorTime = currentCpuTime;
                _lastCpuTime = now;
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
        
        private void ChkDarkMode_Checked(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app) app.SetTheme(true);
        }

        private void ChkDarkMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app) app.SetTheme(false);
        }

        private static float _pgmAudioLevel = 1.0f;
        public static float PgmAudioLevel { get { return _pgmAudioLevel; } set { _pgmAudioLevel = value; } }

        public static float ColorBarsAudioLevel = 1.0f;
        public static float MediaAudioLevel = 1.0f;

        public static AudioState StateMedia = AudioState.AFV;
        public static AudioState StateColorBars = AudioState.AFV;
        
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

        private static double _syntheticAudioPhase = 0;

        public static IntPtr MixAudio(uint audioSampleCount, int activeSourceType, string activeInputName, string caller)
        {
            int totalSamples = (int)audioSampleCount * 2;
            MixerSampleDemand.AddOrUpdate(caller, totalSamples, (_, current) => current + totalSamples);
            int[] mixed = new int[totalSamples];

            Action<string, float, AudioState, bool> mixSource = (name, level, state, isLocalActive) =>
            {
                if (state == AudioState.OFF) return;
                if (state == AudioState.AFV && !isLocalActive) return;
                
                if (name == "Color Bars")
                {
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
                    q.MixInto(mixed, totalSamples, level);
                }
            };

            foreach (var input in DynamicInputs)
            {
                mixSource(input.DisplayName, (float)input.AudioLevel, input.State, activeSourceType == 0 && activeInputName == input.DisplayName);
            }
            mixSource("Media", MediaAudioLevel, StateMedia, activeSourceType == 3);
            mixSource("Color Bars", ColorBarsAudioLevel, StateColorBars, activeSourceType == 1);
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
                if (YtStreamer.IsStreaming || LclRecorder.IsRecording)
                {
                    byte[] audBytes = new byte[audioSampleCount * 4];
                    System.Runtime.InteropServices.Marshal.Copy(modifiedAudioBuffer, audBytes, 0, audBytes.Length);
                    if (YtStreamer.IsStreaming) YtStreamer.PushFrame(audBytes, uyvyBytes);
                    if (LclRecorder.IsRecording) LclRecorder.PushFrame(audBytes, uyvyBytes);
                }
            }
        }

        private static byte[] ConvertBgraToUyvy1080(byte[] bgra)
        {
            const int srcW = 480, dstW = 1920, dstH = 1080;
            byte[] uyvy = new byte[dstH * dstW * 2];
            for (int y = 0; y < dstH; y++)
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
            }
            return uyvy;
        }

        // Pre-computed lookup tables for fast BGRA->UYVY scaling 480x270 -> 1920x1080
        private static readonly int[] _uyvyRowLut = BuildRowLut();
        private static readonly int[] _uyvyColLut = BuildColLut();
        private static int[] BuildRowLut() { var t = new int[1080]; for (int y = 0; y < 1080; y++) t[y] = Math.Min((int)(y * 270.0f / 1080f), 269); return t; }
        private static int[] BuildColLut() { var t = new int[1920]; for (int x = 0; x < 1920; x++) t[x] = Math.Min((int)(x * 480.0f / 1920f), 479); return t; }

        private void InitializeDynamicRouting(DeckLinkDeviceInfo outInfo, System.Collections.Generic.List<DeckLinkDeviceInfo> selectedInputs, System.Windows.Media.Imaging.WriteableBitmap bmpOutput)
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
            foreach (var inInfo in selectedInputs)
            {
                var roleName = "Input " + inputIndex;
                inputIndex++;
                
                var dl = GetLink(inInfo);
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
                    if (_activeSourceType == 0 && _activeInput == inputDev) _activeOutput?.ScheduleFrame(frame, audio, inputDev.AudioLevel);
                };
                inputDev.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == inputDev)
                    {
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                    }
                };
                inputDev.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { vm.AudioL = l; vm.AudioR = r; }); };
                inputDev.StartCapture();
                
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
                        if (_activeSourceType == 1 || _activeSourceType == 2)
                        {
                            // Keep _latestPgmFrame fresh for streaming loop
                            UpdatePgmPreviewSynthetic();

                            if (_activeOutput != null)
                            {
                                _activeOutput.ScheduleSyntheticFrame(_activeSourceType == 1);
                            }
                            else
                            {
                                uint audioSampleCount = 1920;
                                IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName, "synthetic-software-output");
                                if (modifiedAudioBuffer != IntPtr.Zero)
                                {
                                    byte[] uyvyBytes = null;
                                    lock (MainWindow._pgmLock) 
                                    { 
                                        if (_latestPgmFrame != null) uyvyBytes = ConvertBgraToUyvy1080(_latestPgmFrame); 
                                    }
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
                SwitcherPanel.IsEnabled = true;
                CmbOutput.IsEnabled = true;
                InputSelectorsControl.IsEnabled = true;
                BtnApplySettings.IsEnabled = true;
                bool hasAnyHardware = _activeOutput != null || DynamicInputs.Count > 0;
                TxtStatus.Text = hasAnyHardware ? "Running!" : "Running (None selected)";
                if (_activeInput == null && _activeSourceType == 0)
                {
                    ClearPgmPreview();
                }
            }));
        }
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

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
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

        private string _bgColor = "#2962ff";
        public string BgColor { get => _bgColor; set { _bgColor = value; OnPropertyChanged(nameof(BgColor)); } }

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

        public void StartCapture(_BMDDisplayMode displayMode = _BMDDisplayMode.bmdModeHD1080i50)
        {
            if (_isCapturing) return;

            MainWindow.Log($"[{_roleName}] Starting capture with mode: {displayMode}");
            try
            {
                _deckLinkInput.SetCallback(this);
                _deckLinkInput.EnableVideoInput(displayMode, _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoInputFlags.bmdVideoInputFlagDefault);
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
                _deckLinkOutput.CreateVideoFrame(width, height, rowBytes, pixelFormat, _BMDFrameFlags.bmdFrameFlagDefault, out _reusableOutputFrame);
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

                    UIntPtr size = new UIntPtr((uint)(height * rowBytes));
                    memcpy(outputBuffer, inputBuffer, size);
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
                    if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording)
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
                _deckLinkOutput.CreateVideoFrame(width, height, rowBytes, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out _reusableOutputFrame);
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
                UIntPtr size = new UIntPtr((uint)(height * rowBytes));
                memcpy(outputBuffer, inputBuffer, size);
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
                    if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording)
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

        public void ScheduleSyntheticFrame(bool isColorBar)
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
                    if (!isColorBar)
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
                    else
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
                if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording)
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
            MainWindow.Log($"[{_roleName}] Format changed detected.");
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

        public RecordingProfileDefinition(string displayName, string containerExtension, string outputOptions, string videoFilter = null, string fileNameSuffix = null)
        {
            DisplayName = displayName;
            ContainerExtension = containerExtension;
            OutputOptions = outputOptions;
            VideoFilter = videoFilter;
            FileNameSuffix = string.IsNullOrWhiteSpace(fileNameSuffix) ? displayName : fileNameSuffix;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}





