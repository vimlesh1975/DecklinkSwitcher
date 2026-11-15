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
