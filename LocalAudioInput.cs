using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace DecklinkSwitcher
{
    public class LocalAudioInput : IDisposable
    {
        private WaveInEvent _waveIn;
        private string _packetKey;
        
        public Action<double, double> OnAudioLevelArrived;

        public LocalAudioInput(int deviceNumber, string packetKey)
        {
            _packetKey = packetKey;
            _waveIn = new WaveInEvent();
            _waveIn.DeviceNumber = deviceNumber;
            // Native format for microphone, often 44100 or 48000, 1 or 2 channels. 
            // We just request 48000Hz 2-channel directly, WaveIn might give it to us natively.
            _waveIn.WaveFormat = new WaveFormat(48000, 16, 2); 
            _waveIn.DataAvailable += WaveIn_DataAvailable;
            
            // If the microphone doesn't support 48000Hz 2-channel natively, we would need resampler, 
            // but Windows audio subsystem typically handles this resampling automatically when requesting this format!
        }

        public void Start()
        {
            try { _waveIn.StartRecording(); } catch (Exception ex) { MainWindow.Log("Mic Start error: " + ex.Message); }
        }

        public void Stop()
        {
            try { _waveIn.StopRecording(); } catch { }
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                // We have e.Buffer with e.BytesRecorded
                // We must push this into DecklinkSwitcher.MainWindow.LatestAudioPackets["Mic"]
                int sampleCount = e.BytesRecorded / 4; // 16-bit 2-channel = 4 bytes per sample
                short[] arr = new short[sampleCount * 2];
                Buffer.BlockCopy(e.Buffer, 0, arr, 0, e.BytesRecorded);
                
                DecklinkSwitcher.MainWindow.PushAudioPacket(_packetKey, arr);
                
                // Calculate UI levels
                int maxL = 0;
                int maxR = 0;
                for (int i = 0; i < sampleCount * 2; i += 2)
                {
                    int l = Math.Abs((int)arr[i]);
                    int r = Math.Abs((int)arr[i + 1]);
                    if (l > maxL) maxL = l;
                    if (r > maxR) maxR = r;
                }
                double volL = (maxL / 32768.0) * 100.0;
                double volR = (maxR / 32768.0) * 100.0;
                OnAudioLevelArrived?.Invoke(volL, volR);
            }
        }

        public void Dispose()
        {
            Stop();
            _waveIn?.Dispose();
        }
        
        public static System.Collections.Generic.List<MicDeviceInfo> GetDevices()
        {
            var list = new System.Collections.Generic.List<MicDeviceInfo>();
            list.Add(new MicDeviceInfo { DeviceNumber = -1, Name = "None" });
            
            NAudio.CoreAudioApi.MMDeviceCollection mmDevices = null;
            try
            {
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                mmDevices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);
            }
            catch { }

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var caps = WaveIn.GetCapabilities(i);
                string fullName = caps.ProductName;
                
                if (mmDevices != null)
                {
                    foreach (var device in mmDevices)
                    {
                        string mmName = device.FriendlyName;
                        string waveName = caps.ProductName;
                        int len = Math.Min(31, mmName.Length);
                        if (waveName.StartsWith(mmName.Substring(0, len)) || mmName.StartsWith(waveName))
                        {
                            fullName = mmName;
                            break;
                        }
                    }
                }
                if (fullName.IndexOf("DeckLink", StringComparison.OrdinalIgnoreCase) >= 0 || fullName.IndexOf("Blackmagic", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                
                list.Add(new MicDeviceInfo { DeviceNumber = i, Name = fullName });
            }
            return list;
        }
    }
    
    public class MicDeviceInfo
    {
        public int DeviceNumber { get; set; }
        public string Name { get; set; }
    }
}
