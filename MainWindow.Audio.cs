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

    }
}
