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

        private DeckLinkDevice _activeInput;
        private DeckLinkDevice _activeOutput;
        
        private DeckLinkDevice _btn1Input;
        private DeckLinkDevice _btn2Input;
        private DeckLinkDevice _btn3Input;
        private DeckLinkDevice _btn4Input;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpOutput;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpColorBars;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpMatte;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpMedia;
        
        private VlcMediaSource _mediaSource;
        private CancellationTokenSource? _syntheticCts;

        private static string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "decklinkswitcher_log.txt");

        private System.Windows.Threading.DispatcherTimer _seekTimer;
        private bool _isDraggingSeek = false;

        public MainWindow()
        {
            Log("=========================================");
            Log("Application Started");
            try
            {
                InitializeComponent();
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
                        if (_activeOutput != null)
                        {
                            _activeOutput.ScheduleCustomFrame(vBuf, w, h, rb, aBuf, aCount);
                        }
                        else
                        {
                            uint mixedSampleCount = aCount > 0 ? aCount : 1920;
                            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(mixedSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName);
                            if (modifiedAudioBuffer != IntPtr.Zero)
                            {
                                SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, mixedSampleCount);
                                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                            }
                        }
                    }
                };
                _mediaSource.OnPreviewBufferUpdated = (buf) => {
                    Application.Current.Dispatcher.BeginInvoke(() => {
                        _bmpMedia.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
                        if (_activeSourceType == 3) _bmpOutput?.WritePixels(new System.Windows.Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0);
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

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Log("Application closing. Releasing DeckLink resources...");
            
            // Save Settings
            var settings = new AppSettings();
            settings.WindowWidth = this.Width;
            settings.WindowHeight = this.Height;
            if (CmbOutput.SelectedItem is DeckLinkDeviceInfo dout) settings.OutputDevice = dout.DisplayName;
            if (CmbInput1.SelectedItem is DeckLinkDeviceInfo din1) settings.Input1Device = din1.DisplayName;
            if (CmbInput2.SelectedItem is DeckLinkDeviceInfo din2) settings.Input2Device = din2.DisplayName;
            if (CmbInput3.SelectedItem is DeckLinkDeviceInfo din3) settings.Input3Device = din3.DisplayName;
            if (CmbInput4.SelectedItem is DeckLinkDeviceInfo din4) settings.Input4Device = din4.DisplayName;
            settings.AudioLevelPgm = SldPgmAudio.Value;
            settings.AudioLevel1 = SldInput1Audio.Value;
            settings.AudioLevel2 = SldInput2Audio.Value;
            settings.AudioLevel3 = SldInput3Audio.Value;
            settings.AudioLevel4 = SldInput4Audio.Value;
            settings.AudioLevelColorBars = SldColorBarsAudio.Value;
            settings.AudioLevelMedia = SldMediaAudio.Value;
            settings.MatteColorIndex = CmbMatteColor.SelectedIndex;
            settings.SystemAudioMonitorEnabled = ChkSystemAudioOut.IsChecked == true;
            settings.LoopMediaEnabled = ChkLoopMedia.IsChecked == true;
            settings.AudioState1 = (AudioState)CmbAudioState1.SelectedIndex;
            settings.AudioState2 = (AudioState)CmbAudioState2.SelectedIndex;
            settings.AudioState3 = (AudioState)CmbAudioState3.SelectedIndex;
            settings.AudioState4 = (AudioState)CmbAudioState4.SelectedIndex;
            settings.AudioStateColorBars = (AudioState)CmbAudioStateColorBars.SelectedIndex;
            settings.AudioStateMedia = (AudioState)CmbAudioStateMedia.SelectedIndex;
            settings.Save();

            _seekTimer?.Stop();

            // Hide the window immediately so it doesn't freeze on screen
            this.Hide();
            e.Cancel = true;

            // Run the cleanup in the background, then fully exit
            Task.Run(() =>
            {
                _syntheticCts?.Cancel();
                if (_btn1Input != null) _btn1Input.StopCapture();
                if (_btn2Input != null) _btn2Input.StopCapture();
                if (_btn3Input != null) _btn3Input.StopCapture();
                if (_btn4Input != null) _btn4Input.StopCapture();
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
                CmbInput1.ItemsSource = new List<DeckLinkDeviceInfo>(devices);
                CmbInput2.ItemsSource = new List<DeckLinkDeviceInfo>(devices);
                CmbInput3.ItemsSource = new List<DeckLinkDeviceInfo>(devices);
                CmbInput4.ItemsSource = new List<DeckLinkDeviceInfo>(devices);

                var settings = AppSettings.Load();
                
                if (settings.WindowWidth > 0 && !double.IsNaN(settings.WindowWidth)) this.Width = settings.WindowWidth;
                if (settings.WindowHeight > 0 && !double.IsNaN(settings.WindowHeight)) this.Height = settings.WindowHeight;

                int FindDeviceIndex(string name, int defaultIndex)
                {
                    if (string.IsNullOrEmpty(name)) return defaultIndex < devices.Count ? defaultIndex : 0;
                    var idx = devices.FindIndex(d => d.DisplayName == name);
                    return idx >= 0 ? idx : (defaultIndex < devices.Count ? defaultIndex : 0);
                }

                if (physicalCount > 0)
                {
                    CmbOutput.SelectedIndex = FindDeviceIndex(settings.OutputDevice, 1);
                    CmbInput1.SelectedIndex = FindDeviceIndex(settings.Input1Device, devices.Count > 2 ? 2 : 0);
                    CmbInput2.SelectedIndex = FindDeviceIndex(settings.Input2Device, devices.Count > 3 ? 3 : 0);
                    CmbInput3.SelectedIndex = FindDeviceIndex(settings.Input3Device, devices.Count > 4 ? 4 : 0);
                    CmbInput4.SelectedIndex = FindDeviceIndex(settings.Input4Device, devices.Count > 5 ? 5 : 0);
                }
                else
                {
                    CmbOutput.SelectedIndex = 0;
                    CmbInput1.SelectedIndex = 0;
                    CmbInput2.SelectedIndex = 0;
                    CmbInput3.SelectedIndex = 0;
                    CmbInput4.SelectedIndex = 0;
                }
                
                SldPgmAudio.Value = settings.AudioLevelPgm;
                SldInput1Audio.Value = settings.AudioLevel1;
                SldInput2Audio.Value = settings.AudioLevel2;
                SldInput3Audio.Value = settings.AudioLevel3;
                SldInput4Audio.Value = settings.AudioLevel4;
                SldColorBarsAudio.Value = settings.AudioLevelColorBars;
                SldMediaAudio.Value = settings.AudioLevelMedia;
                CmbMatteColor.SelectedIndex = settings.MatteColorIndex;
                ChkSystemAudioOut.IsChecked = settings.SystemAudioMonitorEnabled;
                ChkLoopMedia.IsChecked = settings.LoopMediaEnabled;
                CmbAudioState1.SelectedIndex = (int)settings.AudioState1;
                CmbAudioState2.SelectedIndex = (int)settings.AudioState2;
                CmbAudioState3.SelectedIndex = (int)settings.AudioState3;
                CmbAudioState4.SelectedIndex = (int)settings.AudioState4;
                CmbAudioStateColorBars.SelectedIndex = (int)settings.AudioStateColorBars;
                CmbAudioStateMedia.SelectedIndex = (int)settings.AudioStateMedia;
                
                TxtStatus.Text = "Ready to assign.";
                Log("MainWindow_Loaded completed successfully.");
            }
            catch (Exception ex)
            {
                Log("MainWindow_Loaded error: " + ex.ToString());
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
            var in1Info = CmbInput1.SelectedItem as DeckLinkDeviceInfo;
            var in2Info = CmbInput2.SelectedItem as DeckLinkDeviceInfo;
            var in3Info = CmbInput3.SelectedItem as DeckLinkDeviceInfo;
            var in4Info = CmbInput4.SelectedItem as DeckLinkDeviceInfo;

            if (outInfo == null)
            {
                TxtStatus.Text = "Please select an Output option (or None).";
                return;
            }

            // Stop synthetic timer & existing devices if any
            _syntheticCts?.Cancel();
            if (_activeOutput != null) _activeOutput.StopPlayback();
            if (_btn1Input != null) _btn1Input.StopCapture();
            if (_btn2Input != null) _btn2Input.StopCapture();
            if (_btn3Input != null) _btn3Input.StopCapture();
            if (_btn4Input != null) _btn4Input.StopCapture();
            
            _activeOutput = _btn1Input = _btn2Input = _btn3Input = _btn4Input = null;
            
            System.Windows.Media.Imaging.WriteableBitmap bmp1 = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            System.Windows.Media.Imaging.WriteableBitmap bmp2 = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            System.Windows.Media.Imaging.WriteableBitmap bmp3 = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            System.Windows.Media.Imaging.WriteableBitmap bmp4 = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bmpOutput = new System.Windows.Media.Imaging.WriteableBitmap(480, 270, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

            Preview1.Source = bmp1;
            Preview2.Source = bmp2;
            Preview3.Source = bmp3;
            Preview4.Source = bmp4;
            PreviewOutput.Source = _bmpOutput;

            SwitcherPanel.IsEnabled = false;
            TxtStatus.Text = "Initializing...";

            Thread mtaThread = new Thread(() =>
            {
                try
                {
                    InitializeDynamicRouting(outInfo, in1Info, in2Info, in3Info, in4Info, bmp1, bmp2, bmp3, bmp4, _bmpOutput);
                }
                catch (Exception ex)
                {
                    Log($"MTA Init Error: {ex.Message}");
                }
            });
            mtaThread.SetApartmentState(ApartmentState.MTA);
            mtaThread.IsBackground = true;
            mtaThread.Start();
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

        private void BtnInput1_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 0; ActiveSourceType = 0;
            _activeInput = _btn1Input; ActiveInputName = "Input 1";
            if (_btn1Input != null)
            {
                Log("Switched to Input 1");
            }
            else
            {
                Log("Switched to Input 1 (None)");
                ClearPgmPreview();
            }
        }

        private void BtnInput2_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 0; ActiveSourceType = 0;
            _activeInput = _btn2Input; ActiveInputName = "Input 2";
            if (_btn2Input != null)
            {
                Log("Switched to Input 2");
            }
            else
            {
                Log("Switched to Input 2 (None)");
                ClearPgmPreview();
            }
        }

        private void BtnInput3_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 0; ActiveSourceType = 0;
            _activeInput = _btn3Input; ActiveInputName = "Input 3";
            if (_btn3Input != null)
            {
                Log("Switched to Input 3");
            }
            else
            {
                Log("Switched to Input 3 (None)");
                ClearPgmPreview();
            }
        }

        private void BtnInput4_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 0; ActiveSourceType = 0;
            _activeInput = _btn4Input; ActiveInputName = "Input 4";
            if (_btn4Input != null)
            {
                Log("Switched to Input 4");
            }
            else
            {
                Log("Switched to Input 4 (None)");
                ClearPgmPreview();
            }
        }

        private void Preview1_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput1_Click(null, null);
        private void Preview2_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput2_Click(null, null);
        private void Preview3_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput3_Click(null, null);
        private void Preview4_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput4_Click(null, null);

        private void BtnColorBars_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 1; ActiveSourceType = 1; ActiveInputName = "Color Bars";
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
            Log("Switched to Matte");
            UpdatePgmPreviewSynthetic();
            AudioOutputL.Value = 0;
            AudioOutputR.Value = 0;
        }
        private void PreviewMatte_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnMatte_Click(null, null);

        private void BtnMedia_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 3; ActiveSourceType = 3; ActiveInputName = "Media"; Log("Switched to Local Video");
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
        }

        private void ChkSystemAudioOut_Unchecked(object sender, RoutedEventArgs e)
        {
            SystemAudioPlayer.IsEnabled = false;
        }

        private static float _pgmAudioLevel = 1.0f;
        public static float PgmAudioLevel { get { return _pgmAudioLevel; } set { _pgmAudioLevel = value; } }

        public static float ColorBarsAudioLevel = 1.0f;
        public static float MediaAudioLevel = 1.0f;
        public static float Input1AudioLevel = 1.0f;
        public static float Input2AudioLevel = 1.0f;
        public static float Input3AudioLevel = 1.0f;
        public static float Input4AudioLevel = 1.0f;
        
        public static AudioState State1 = AudioState.AFV;
        public static AudioState State2 = AudioState.AFV;
        public static AudioState State3 = AudioState.AFV;
        public static AudioState State4 = AudioState.AFV;
        public static AudioState StateMedia = AudioState.AFV;
        public static AudioState StateColorBars = AudioState.AFV;
        
        public static System.Collections.Concurrent.ConcurrentDictionary<string, short[]> LatestAudioPackets = new();

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
        
        private void SldMediaAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_mediaSource != null) _mediaSource.AudioLevel = (float)e.NewValue; }

        private void SldInput1Audio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { Input1AudioLevel = (float)e.NewValue; if (_btn1Input != null) _btn1Input.AudioLevel = (float)e.NewValue; }
        private void SldInput2Audio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { Input2AudioLevel = (float)e.NewValue; if (_btn2Input != null) _btn2Input.AudioLevel = (float)e.NewValue; }
        private void SldInput3Audio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { Input3AudioLevel = (float)e.NewValue; if (_btn3Input != null) _btn3Input.AudioLevel = (float)e.NewValue; }
        private void SldInput4Audio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { Input4AudioLevel = (float)e.NewValue; if (_btn4Input != null) _btn4Input.AudioLevel = (float)e.NewValue; }

        private void CmbAudioState_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            State1 = (AudioState)CmbAudioState1.SelectedIndex;
            State2 = (AudioState)CmbAudioState2.SelectedIndex;
            State3 = (AudioState)CmbAudioState3.SelectedIndex;
            State4 = (AudioState)CmbAudioState4.SelectedIndex;
            StateMedia = (AudioState)CmbAudioStateMedia.SelectedIndex;
            StateColorBars = (AudioState)CmbAudioStateColorBars.SelectedIndex;
        }

        private static double _syntheticAudioPhase = 0;

        public static IntPtr MixAudio(uint audioSampleCount, int activeSourceType, string activeInputName)
        {
            int totalSamples = (int)audioSampleCount * 2;
            int[] mixed = new int[totalSamples];

            Action<string, float, AudioState, bool> mixSource = (name, level, state, isLocalActive) =>
            {
                if (state == AudioState.OFF) return;
                if (state == AudioState.AFV && !isLocalActive) return;
                
                if (name == "Color Bars")
                {
                    for (int i = 0; i < totalSamples; i += 2)
                    {
                        short sample = (short)(Math.Sin(_syntheticAudioPhase) * 16384 * level);
                        mixed[i] += sample;
                        mixed[i+1] += sample;
                        _syntheticAudioPhase += 2.0 * Math.PI * 1000.0 / 48000.0;
                        if (_syntheticAudioPhase >= 2.0 * Math.PI) _syntheticAudioPhase -= 2.0 * Math.PI;
                    }
                    return;
                }

                if (LatestAudioPackets.TryGetValue(name, out short[] packet) && packet != null)
                {
                    int toMix = Math.Min(totalSamples, packet.Length);
                    int sum = 0;
                    for (int i = 0; i < toMix; i++)
                    {
                        mixed[i] += (int)(packet[i] * level);
                        sum += Math.Abs(packet[i]);
                    }
                    if (sum > 0 && name == "Media") MainWindow.Log($"Mixed Media packet! Length: {packet.Length}, Sum: {sum}");
                    LatestAudioPackets[name] = null; // Consume the packet
                }
            };

            mixSource("Input 1", Input1AudioLevel, State1, activeSourceType == 0 && activeInputName == "Input 1");
            mixSource("Input 2", Input2AudioLevel, State2, activeSourceType == 0 && activeInputName == "Input 2");
            mixSource("Input 3", Input3AudioLevel, State3, activeSourceType == 0 && activeInputName == "Input 3");
            mixSource("Input 4", Input4AudioLevel, State4, activeSourceType == 0 && activeInputName == "Input 4");
            mixSource("Media", MediaAudioLevel, StateMedia, activeSourceType == 3);
            mixSource("Color Bars", ColorBarsAudioLevel, StateColorBars, activeSourceType == 1);

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



        private void InitializeDynamicRouting(DeckLinkDeviceInfo outInfo, DeckLinkDeviceInfo in1Info, DeckLinkDeviceInfo in2Info, DeckLinkDeviceInfo in3Info, DeckLinkDeviceInfo in4Info, System.Windows.Media.Imaging.WriteableBitmap bmp1, System.Windows.Media.Imaging.WriteableBitmap bmp2, System.Windows.Media.Imaging.WriteableBitmap bmp3, System.Windows.Media.Imaging.WriteableBitmap bmp4, System.Windows.Media.Imaging.WriteableBitmap bmpOutput)
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

            if (in1Info != null && in1Info.Index >= 0 && GetLink(in1Info) is IDeckLink link1)
            {
                _btn1Input = new DeckLinkDevice(link1, "Input 1");
                _btn1Input.PreviewBitmap = bmp1;
                _btn1Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn1Input) _activeOutput?.ScheduleFrame(frame, audio, _btn1Input.AudioLevel);
                };
                _btn1Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn1Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn1Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar1L.Value = l; AudioBar1R.Value = r; }); };
                _btn1Input.StartCapture();
            }
            else
            {
                _btn1Input = null;
            }

            if (in2Info != null && in2Info.Index >= 0 && GetLink(in2Info) is IDeckLink link2)
            {
                _btn2Input = new DeckLinkDevice(link2, "Input 2");
                _btn2Input.PreviewBitmap = bmp2;
                _btn2Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn2Input) _activeOutput?.ScheduleFrame(frame, audio, _btn2Input.AudioLevel);
                };
                _btn2Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn2Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn2Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar2L.Value = l; AudioBar2R.Value = r; }); };
                _btn2Input.StartCapture();
            }
            else
            {
                _btn2Input = null;
            }

            if (in3Info != null && in3Info.Index >= 0 && GetLink(in3Info) is IDeckLink link3)
            {
                _btn3Input = new DeckLinkDevice(link3, "Input 3");
                _btn3Input.PreviewBitmap = bmp3;
                _btn3Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn3Input) _activeOutput?.ScheduleFrame(frame, audio, _btn3Input.AudioLevel);
                };
                _btn3Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn3Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn3Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar3L.Value = l; AudioBar3R.Value = r; }); };
                _btn3Input.StartCapture();
            }
            else
            {
                _btn3Input = null;
            }

            if (in4Info != null && in4Info.Index >= 0 && GetLink(in4Info) is IDeckLink link4)
            {
                _btn4Input = new DeckLinkDevice(link4, "Input 4");
                _btn4Input.PreviewBitmap = bmp4;
                _btn4Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn4Input) _activeOutput?.ScheduleFrame(frame, audio, _btn4Input.AudioLevel);
                };
                _btn4Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn4Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn4Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar4L.Value = l; AudioBar4R.Value = r; }); };
                _btn4Input.StartCapture();
            }
            else
            {
                _btn4Input = null;
            }

            _activeInput = _btn1Input ?? _btn2Input ?? _btn3Input ?? _btn4Input;

            _syntheticCts?.Cancel();
            _syntheticCts = new CancellationTokenSource();
            var token = _syntheticCts.Token;

            Task.Run(async () =>
            {
                double syntheticAudioPhase = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_activeSourceType == 1 || _activeSourceType == 2)
                        {
                            if (_activeOutput != null)
                            {
                                _activeOutput.ScheduleSyntheticFrame(_activeSourceType == 1);
                            }
                            else
                            {
                                uint audioSampleCount = 1920;
                                IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName);
                                if (modifiedAudioBuffer != IntPtr.Zero)
                                {
                                    SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, audioSampleCount);
                                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(modifiedAudioBuffer);
                                }
                            }
                        }
                        await Task.Delay(40, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, token);

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SwitcherPanel.IsEnabled = true;
                bool hasAnyHardware = _activeOutput != null || _btn1Input != null || _btn2Input != null || _btn3Input != null || _btn4Input != null;
                TxtStatus.Text = hasAnyHardware ? "Running!" : "Running (None selected)";
                BtnInput1.Content = (in1Info != null && in1Info.Index >= 0) ? in1Info.DisplayName : "Input 1 (None)";
                BtnInput2.Content = (in2Info != null && in2Info.Index >= 0) ? in2Info.DisplayName : "Input 2 (None)";
                BtnInput3.Content = (in3Info != null && in3Info.Index >= 0) ? in3Info.DisplayName : "Input 3 (None)";
                BtnInput4.Content = (in4Info != null && in4Info.Index >= 0) ? in4Info.DisplayName : "Input 4 (None)";
                if (_activeInput == null && _activeSourceType == 0)
                {
                    ClearPgmPreview();
                }
            }));
        }
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
            _deckLinkOutput.DisableVideoOutput();
            _deckLinkOutput.DisableAudioOutput();
            _isPlaying = false;
        }

        private IDeckLinkMutableVideoFrame _reusableOutputFrame;

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

            if (Interlocked.CompareExchange(ref _isDisplaying, 1, 0) == 1)
            {
                // Drop frame, currently displaying
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

            // Extract audio data
            uint audioSampleCount = audioPacket != null ? (uint)audioPacket.GetSampleFrameCount() : 1920;
            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName);

            System.Threading.Tasks.Task.Run(() => 
            {
                try 
                {
                    _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame);
                    if (modifiedAudioBuffer != IntPtr.Zero && audioSampleCount > 0)
                    {
                        _deckLinkOutput.WriteAudioSamplesSync(modifiedAudioBuffer, audioSampleCount, out uint written);
                        SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, audioSampleCount);
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

            if (Interlocked.CompareExchange(ref _isDisplaying, 1, 0) == 1) return;

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

            uint mixedSampleCount = audioSampleCount > 0 ? audioSampleCount : 1920;
            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(mixedSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName);

            System.Threading.Tasks.Task.Run(() => 
            {
                try 
                {
                    _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame);
                    if (modifiedAudioBuffer != IntPtr.Zero && mixedSampleCount > 0)
                    {
                        _deckLinkOutput.WriteAudioSamplesSync(modifiedAudioBuffer, mixedSampleCount, out uint written);
                        SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, mixedSampleCount);
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
            if (Interlocked.CompareExchange(ref _isDisplaying, 1, 0) == 1)
            {
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
            uint audioSampleCount = 1920;
            IntPtr modifiedAudioBuffer = MainWindow.MixAudio(audioSampleCount, MainWindow.ActiveSourceType, MainWindow.ActiveInputName);

            System.Threading.Tasks.Task.Run(() => 
            {
                try 
                {
                    _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame);
                    if (modifiedAudioBuffer != IntPtr.Zero && audioSampleCount > 0)
                    {
                        _deckLinkOutput.WriteAudioSamplesSync(modifiedAudioBuffer, audioSampleCount, out uint written);
                        SystemAudioPlayer.WriteAudio(modifiedAudioBuffer, audioSampleCount);
                    }
                } 
                catch (Exception ex) 
                {
                    MainWindow.Log($"[{_roleName}] Task.Run DisplaySyntheticVideo/Audio error: {ex.Message}");
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
                    if (PreviewBitmap != null && Environment.TickCount - _lastPreviewTicks > 200) // 5 fps
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
                        MainWindow.LatestAudioPackets[_roleName] = arr;

                        if (OnAudioLevelArrived != null && Environment.TickCount - _lastPreviewTicks <= 200) 
                        {
                            unsafe 
                            {
                                short* samples = (short*)audioPtr.ToPointer();
                                int maxL = 0;
                                int maxR = 0;
                                for (int i = 0; i < sampleCount * 2; i += 2) // 2 channels
                                {
                                    int valL = Math.Abs(samples[i]);
                                    int valR = Math.Abs(samples[i+1]);
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
}
