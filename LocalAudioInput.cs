using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DecklinkSwitcher
{
    public class LocalAudioInput : IDisposable
    {
        private MMDeviceEnumerator _deviceEnumerator;
        private MMDevice _device;
        private WasapiRecorder _capture;
        private CancellationTokenSource _captureCancellation;
        private Task _captureTask;
        private string _packetKey;
        
        public Action<double, double> OnAudioLevelArrived;

        public LocalAudioInput(string deviceId, string packetKey)
        {
            _packetKey = packetKey;
            _deviceEnumerator = new MMDeviceEnumerator();
            _device = _deviceEnumerator.GetDevice(deviceId);
            _capture = new WasapiRecorderBuilder()
                .WithDevice(_device)
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(50)
                .WithFormat(new WaveFormat(48000, 16, 2))
                .WithMmcssThreadPriority("Audio")
                .Build();
        }

        public void Start()
        {
            try
            {
                _captureCancellation = new CancellationTokenSource();
                _captureTask = Task.Run(() => CaptureAsync(_captureCancellation.Token));
            }
            catch (Exception ex)
            {
                MainWindow.Log("Mic Start error: " + ex.Message);
            }
        }

        public void Stop()
        {
            _captureCancellation?.Cancel();
            try { _captureTask?.GetAwaiter().GetResult(); } catch { }
            _captureCancellation?.Dispose();
            _captureCancellation = null;
            _captureTask = null;
        }

        private async Task CaptureAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var audioBuffer in _capture.CaptureAsync(cancellationToken))
                {
                    ProcessBuffer(audioBuffer.Data);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                MainWindow.Log("Mic Capture error: " + ex.Message);
            }
        }

        private void ProcessBuffer(ReadOnlyMemory<byte> buffer)
        {
            int bytesRecorded = buffer.Length;
            if (bytesRecorded > 0)
            {
                int sampleCount = bytesRecorded / 4;
                short[] arr = MemoryMarshal.Cast<byte, short>(buffer.Span[..(sampleCount * 4)]).ToArray();
                
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
            _capture?.Dispose();
            _device?.Dispose();
            _deviceEnumerator?.Dispose();
        }
        
        public static System.Collections.Generic.List<MicDeviceInfo> GetDevices()
        {
            var list = new System.Collections.Generic.List<MicDeviceInfo>();
            list.Add(new MicDeviceInfo { DeviceNumber = -1, Name = "None" });
            
            MMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                for (int i = 0; i < devices.Count; i++)
                {
                    var device = devices[i];
                    string name = device.FriendlyName;
                    if (name.IndexOf("DeckLink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Blackmagic", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    list.Add(new MicDeviceInfo { DeviceNumber = i, Name = name, DeviceId = device.ID });
                }
            }
            catch { }
            enumerator?.Dispose();
            return list;
        }
    }
    
    public class MicDeviceInfo
    {
        public int DeviceNumber { get; set; }
        public string Name { get; set; }
        public string DeviceId { get; set; }
    }
}
