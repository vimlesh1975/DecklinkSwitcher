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
        private DeckLinkDevice _decklink4K;
        private DeckLinkDevice _decklinkDuo1;
        private DeckLinkDevice _decklinkDuo2;
        private DeckLinkDevice _decklinkDuo3;
        private DeckLinkDevice _decklinkDuo4;

        private DeckLinkDevice _activeInput;
        private DeckLinkDevice _activeOutput;
        
        private DeckLinkDevice _btn1Input;
        private DeckLinkDevice _btn2Input;
        private DeckLinkDevice _btn3Input;
        private DeckLinkDevice _btn4Input;
        private System.Windows.Media.Imaging.WriteableBitmap _bmpOutput;

        private static string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "decklinkswitcher_log.txt");

        public MainWindow()
        {
            Log("=========================================");
            Log("Application Started");
            InitializeComponent();
            
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Log("Application closing. Releasing DeckLink resources...");
            
            // Hide the window immediately so it doesn't freeze on screen
            this.Hide();
            e.Cancel = true;

            // Run the cleanup in the background, then fully exit
            Task.Run(() =>
            {
                if (_btn1Input != null) _btn1Input.StopCapture();
                if (_btn2Input != null) _btn2Input.StopCapture();
                if (_btn3Input != null) _btn3Input.StopCapture();
                if (_btn4Input != null) _btn4Input.StopCapture();
                if (_activeOutput != null) _activeOutput.StopPlayback();
                Log("Resources released.");
                Environment.Exit(0);
            });
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Log("Window loaded. Discovering DeckLink devices...");
            
            List<DeckLinkDeviceInfo> devices = new List<DeckLinkDeviceInfo>();
            IDeckLinkIterator deckLinkIterator = new CDeckLinkIterator();
            if (deckLinkIterator != null)
            {
                while (true)
                {
                    deckLinkIterator.Next(out IDeckLink deckLink);
                    if (deckLink == null) break;

                    deckLink.GetModelName(out string modelName);
                    // Differentiate Duo ports by adding a unique identifier or just listing them
                    // Since the API returns multiple identical "DeckLink Duo 2" names, we can append an index
                    string uniqueName = $"{modelName} (Port {devices.Count + 1})";
                    devices.Add(new DeckLinkDeviceInfo { DisplayName = uniqueName, Index = devices.Count });
                }
            }

            if (devices.Count == 0)
            {
                Log("No DeckLink devices found.");
                TxtStatus.Text = "No devices found.";
                return;
            }

            CmbOutput.ItemsSource = new List<DeckLinkDeviceInfo>(devices);
            CmbInput1.ItemsSource = new List<DeckLinkDeviceInfo>(devices);
            CmbInput2.ItemsSource = new List<DeckLinkDeviceInfo>(devices);
            CmbInput3.ItemsSource = new List<DeckLinkDeviceInfo>(devices);
            CmbInput4.ItemsSource = new List<DeckLinkDeviceInfo>(devices);

            if (devices.Count > 0) CmbOutput.SelectedIndex = 0;
            if (devices.Count > 1) CmbInput1.SelectedIndex = 1;
            if (devices.Count > 2) CmbInput2.SelectedIndex = 2;
            if (devices.Count > 3) CmbInput3.SelectedIndex = 3;
            if (devices.Count > 4) CmbInput4.SelectedIndex = 4;
            
            TxtStatus.Text = "Ready to assign.";
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

            if (outInfo == null || in1Info == null)
            {
                TxtStatus.Text = "Must select Output and at least Input 1.";
                return;
            }

            // Stop existing if any
            if (_decklink4K != null) _decklink4K.StopCapture();
            if (_decklinkDuo2 != null) _decklinkDuo2.StopCapture();
            if (_decklinkDuo3 != null) _decklinkDuo3.StopCapture();
            if (_decklinkDuo4 != null) _decklinkDuo4.StopCapture();
            if (_decklinkDuo1 != null) _decklinkDuo1.StopPlayback();
            
            _decklinkDuo1 = _decklink4K = _decklinkDuo2 = _decklinkDuo3 = _decklinkDuo4 = null;
            
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

        private int _activeSourceType = 0; // 0=Device, 1=ColorBars, 2=Matte

        private void BtnInput1_Click(object sender, RoutedEventArgs e)
        {
            if (_btn1Input != null) { _activeSourceType = 0; _activeInput = _btn1Input; Log("Switched to Input 1"); }
        }

        private void BtnInput2_Click(object sender, RoutedEventArgs e)
        {
            if (_btn2Input != null) { _activeSourceType = 0; _activeInput = _btn2Input; Log("Switched to Input 2"); }
        }

        private void BtnInput3_Click(object sender, RoutedEventArgs e)
        {
            if (_btn3Input != null) { _activeSourceType = 0; _activeInput = _btn3Input; Log("Switched to Input 3"); }
        }

        private void BtnInput4_Click(object sender, RoutedEventArgs e)
        {
            if (_btn4Input != null) { _activeSourceType = 0; _activeInput = _btn4Input; Log("Switched to Input 4"); }
        }

        private void Preview1_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput1_Click(null, null);
        private void Preview2_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput2_Click(null, null);
        private void Preview3_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput3_Click(null, null);
        private void Preview4_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BtnInput4_Click(null, null);

        private void BtnColorBars_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 1; Log("Switched to Color Bars");
            UpdatePgmPreviewSynthetic();
        }

        private void BtnMatte_Click(object sender, RoutedEventArgs e)
        {
            _activeSourceType = 2; Log("Switched to Matte");
            UpdatePgmPreviewSynthetic();
        }

        public static byte MatteY = 41;
        public static byte MatteU = 212;
        public static byte MatteV = 114;

        private static float _pgmAudioLevel = 1.0f;
        public static float PgmAudioLevel { get { return _pgmAudioLevel; } set { _pgmAudioLevel = value; } }

        private void SldPgmAudio_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PgmAudioLevel = (float)e.NewValue;
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



        private void InitializeDynamicRouting(DeckLinkDeviceInfo outInfo, DeckLinkDeviceInfo in1Info, DeckLinkDeviceInfo in2Info, DeckLinkDeviceInfo in3Info, DeckLinkDeviceInfo in4Info, System.Windows.Media.Imaging.WriteableBitmap bmp1, System.Windows.Media.Imaging.WriteableBitmap bmp2, System.Windows.Media.Imaging.WriteableBitmap bmp3, System.Windows.Media.Imaging.WriteableBitmap bmp4, System.Windows.Media.Imaging.WriteableBitmap bmpOutput)
        {
            IDeckLinkIterator iterator = new CDeckLinkIterator();
            List<IDeckLink> mtaLinks = new List<IDeckLink>();
            while (true)
            {
                iterator.Next(out IDeckLink dl);
                if (dl == null) break;
                mtaLinks.Add(dl);
            }

            IDeckLink GetLink(DeckLinkDeviceInfo info)
            {
                if (info == null || info.Index >= mtaLinks.Count) return null;
                return mtaLinks[info.Index];
            }

            _activeOutput = new DeckLinkDevice(GetLink(outInfo), outInfo.DisplayName);
            _activeOutput.StartPlayback();

            if (in1Info != null)
            {
                _btn1Input = new DeckLinkDevice(GetLink(in1Info), in1Info.DisplayName);
                _btn1Input.PreviewBitmap = bmp1;
                _btn1Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn1Input) _activeOutput.ScheduleFrame(frame, audio);
                    else if (_activeSourceType == 1 && _activeInput == _btn1Input) _activeOutput.ScheduleSyntheticFrame(true);
                    else if (_activeSourceType == 2 && _activeInput == _btn1Input) _activeOutput.ScheduleSyntheticFrame(false);
                };
                _btn1Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn1Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn1Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar1L.Value = l; AudioBar1R.Value = r; if (_activeSourceType == 0 && _activeInput == _btn1Input) { AudioOutputL.Value = l; AudioOutputR.Value = r; } }); };
                _btn1Input.StartCapture();
            }

            if (in2Info != null)
            {
                _btn2Input = new DeckLinkDevice(GetLink(in2Info), in2Info.DisplayName);
                _btn2Input.PreviewBitmap = bmp2;
                _btn2Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn2Input) _activeOutput.ScheduleFrame(frame, audio);
                    else if (_activeSourceType == 1 && _activeInput == _btn2Input) _activeOutput.ScheduleSyntheticFrame(true);
                    else if (_activeSourceType == 2 && _activeInput == _btn2Input) _activeOutput.ScheduleSyntheticFrame(false);
                };
                _btn2Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn2Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn2Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar2L.Value = l; AudioBar2R.Value = r; if (_activeSourceType == 0 && _activeInput == _btn2Input) { AudioOutputL.Value = l; AudioOutputR.Value = r; } }); };
                _btn2Input.StartCapture();
            }

            if (in3Info != null)
            {
                _btn3Input = new DeckLinkDevice(GetLink(in3Info), in3Info.DisplayName);
                _btn3Input.PreviewBitmap = bmp3;
                _btn3Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn3Input) _activeOutput.ScheduleFrame(frame, audio);
                    else if (_activeSourceType == 1 && _activeInput == _btn3Input) _activeOutput.ScheduleSyntheticFrame(true);
                    else if (_activeSourceType == 2 && _activeInput == _btn3Input) _activeOutput.ScheduleSyntheticFrame(false);
                };
                _btn3Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn3Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn3Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar3L.Value = l; AudioBar3R.Value = r; if (_activeSourceType == 0 && _activeInput == _btn3Input) { AudioOutputL.Value = l; AudioOutputR.Value = r; } }); };
                _btn3Input.StartCapture();
            }

            if (in4Info != null)
            {
                _btn4Input = new DeckLinkDevice(GetLink(in4Info), in4Info.DisplayName);
                _btn4Input.PreviewBitmap = bmp4;
                _btn4Input.OnVideoAndAudioArrived = (frame, audio) => 
                {
                    if (_activeSourceType == 0 && _activeInput == _btn4Input) _activeOutput.ScheduleFrame(frame, audio);
                    else if (_activeSourceType == 1 && _activeInput == _btn4Input) _activeOutput.ScheduleSyntheticFrame(true);
                    else if (_activeSourceType == 2 && _activeInput == _btn4Input) _activeOutput.ScheduleSyntheticFrame(false);
                };
                _btn4Input.OnPreviewBufferUpdated = (buf) => 
                { 
                    if (_activeSourceType == 0 && _activeInput == _btn4Input) 
                        bmpOutput.WritePixels(new Int32Rect(0, 0, 480, 270), buf, 480 * 4, 0); 
                };
                _btn4Input.OnAudioLevelArrived = (l, r) => { Application.Current.Dispatcher.BeginInvoke(() => { AudioBar4L.Value = l; AudioBar4R.Value = r; if (_activeSourceType == 0 && _activeInput == _btn4Input) { AudioOutputL.Value = l; AudioOutputR.Value = r; } }); };
                _btn4Input.StartCapture();
            }

            _activeInput = _btn1Input;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SwitcherPanel.IsEnabled = true;
                TxtStatus.Text = "Running!";
                BtnInput1.Content = in1Info?.DisplayName ?? "Input 1";
                BtnInput2.Content = in2Info?.DisplayName ?? "Input 2";
                BtnInput3.Content = in3Info?.DisplayName ?? "Input 3";
                BtnInput4.Content = in4Info?.DisplayName ?? "Input 4";
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

        public void ScheduleFrame(IDeckLinkVideoInputFrame inputFrame, IDeckLinkAudioInputPacket audioPacket)
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
            IntPtr audioBuffer = IntPtr.Zero;
            uint audioSampleCount = 0;
            IntPtr modifiedAudioBuffer = IntPtr.Zero;

            if (audioPacket != null)
            {
                audioPacket.GetBytes(out audioBuffer);
                audioSampleCount = (uint)audioPacket.GetSampleFrameCount();

                if (MainWindow.PgmAudioLevel != 1.0f && audioSampleCount > 0)
                {
                    modifiedAudioBuffer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem((int)audioSampleCount * 4); // 2 channels, 16-bit
                    unsafe
                    {
                        short* srcPtr = (short*)audioBuffer.ToPointer();
                        short* dstPtr = (short*)modifiedAudioBuffer.ToPointer();
                        float level = MainWindow.PgmAudioLevel;
                        int totalSamples = (int)audioSampleCount * 2;
                        for (int i = 0; i < totalSamples; i++)
                        {
                            float sample = srcPtr[i] * level;
                            if (sample > 32767) sample = 32767;
                            else if (sample < -32768) sample = -32768;
                            dstPtr[i] = (short)sample;
                        }
                    }
                    audioBuffer = modifiedAudioBuffer;
                }
            }

            System.Threading.Tasks.Task.Run(() => 
            {
                try 
                {
                    _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame);
                    if (audioBuffer != IntPtr.Zero && audioSampleCount > 0)
                    {
                        _deckLinkOutput.WriteAudioSamplesSync(audioBuffer, audioSampleCount, out uint written);
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

            // Generate synthetic audio
            uint audioSampleCount = 1920; // 48000 Hz / 25 fps = 1920
            IntPtr audioBuffer = IntPtr.Zero;
            
            if (isColorBar)
            {
                audioBuffer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem((int)audioSampleCount * 4); // 2 channels, 2 bytes/sample
                unsafe
                {
                    short* audioPtr = (short*)audioBuffer.ToPointer();
                    for (int i = 0; i < audioSampleCount; i++)
                    {
                        double val = Math.Sin(_audioPhase) * 16384 * MainWindow.PgmAudioLevel; // Half volume, adjusted by PGM level
                        short sample = (short)val;
                        audioPtr[i * 2] = sample; // Left
                        audioPtr[i * 2 + 1] = sample; // Right
                        _audioPhase += 2.0 * Math.PI * 1000.0 / 48000.0;
                        if (_audioPhase >= 2.0 * Math.PI) _audioPhase -= 2.0 * Math.PI;
                    }
                }
            }
            
            System.Threading.Tasks.Task.Run(() => 
            {
                try 
                {
                    _deckLinkOutput.DisplayVideoFrameSync(_reusableOutputFrame);
                    if (audioBuffer != IntPtr.Zero)
                    {
                        _deckLinkOutput.WriteAudioSamplesSync(audioBuffer, audioSampleCount, out uint written);
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(audioBuffer);
                    }
                } 
                catch (Exception ex) 
                {
                    MainWindow.Log($"[{_roleName}] Task.Run DisplayVideo/Audio error: {ex.Message}");
                }
                finally
                {
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

                    if (audioPacket != null && OnAudioLevelArrived != null && Environment.TickCount - _lastPreviewTicks <= 200) 
                    {
                        audioPacket.GetBytes(out IntPtr audioPtr);
                        int sampleCount = (int)audioPacket.GetSampleFrameCount();
                        
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
