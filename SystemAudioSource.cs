using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DecklinkSwitcher
{
    public class SystemAudioSource : IDisposable
    {
        private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");

        private WasapiLoopbackCapture? _capture;
        private readonly string _sourceName = "System Audio";
        private bool _isDisposed = false;
        private MMDevice? _selectedDevice;
        private System.Threading.Timer? _decayTimer;
        private long _lastBufferTime = 0;

        public Action<double, double>? OnAudioLevelArrived;

        public string SourceName => _sourceName;
        public bool IsRunning => _capture != null;

        public void Start(string? deviceId = null)
        {
            try
            {
                Stop();

                MMDevice? targetDevice = null;
                using var enumerator = new MMDeviceEnumerator();
                if (!string.IsNullOrEmpty(deviceId))
                {
                    try { targetDevice = enumerator.GetDevice(deviceId); } catch { }
                }

                if (targetDevice == null)
                {
                    try { targetDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
                }

                if (targetDevice == null)
                {
                    MainWindow.Log("SystemAudioSource: No render device found for loopback capture.");
                    return;
                }

                _selectedDevice = targetDevice;
                _capture = new WasapiLoopbackCapture(targetDevice);
                var waveFormat = _capture.WaveFormat;

                _capture.DataAvailable += (s, e) =>
                {
                    try
                    {
                        ProcessBuffer(e.Buffer, e.BytesRecorded);
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"SystemAudioSource buffer error: {ex.Message}");
                    }
                };

                _capture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null && !_isDisposed)
                    {
                        MainWindow.Log($"SystemAudioSource recording stopped with error: {e.Exception.Message}");
                    }
                };

                _capture.StartRecording();
                _lastBufferTime = System.Diagnostics.Stopwatch.GetTimestamp();
                _decayTimer?.Dispose();
                _decayTimer = new System.Threading.Timer(_ =>
                {
                    if (System.Diagnostics.Stopwatch.GetTimestamp() - _lastBufferTime > (System.Diagnostics.Stopwatch.Frequency / 4))
                    {
                        OnAudioLevelArrived?.Invoke(0, 0);
                    }
                }, null, 100, 100);

                MainWindow.Log($"SystemAudioSource started on device '{targetDevice.FriendlyName}' ({waveFormat.SampleRate}Hz, {waveFormat.Channels}ch, {waveFormat.BitsPerSample}-bit)");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"SystemAudioSource Start error: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                _decayTimer?.Dispose();
                _decayTimer = null;
                if (_capture != null)
                {
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                }
                _selectedDevice?.Dispose();
                _selectedDevice = null;
            }
            catch { }
        }

        private void ProcessBuffer(byte[] buffer, int bytesRecorded)
        {
            if (bytesRecorded <= 0 || _capture == null) return;
            _lastBufferTime = System.Diagnostics.Stopwatch.GetTimestamp();

            var wf = _capture.WaveFormat;
            int channels = wf.Channels;
            int sampleRate = wf.SampleRate;
            int bitsPerSample = wf.BitsPerSample;

            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
            if (!isFloat && wf is WaveFormatExtensible wfe)
            {
                isFloat = (wfe.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
            }

            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0) return;
            int frameSize = channels * bytesPerSample;
            if (frameSize <= 0) return;

            int inFrameCount = bytesRecorded / frameSize;
            if (inFrameCount <= 0) return;

            // Extract float samples to intermediate stereo buffer
            float[] intermediate = new float[inFrameCount * 2];
            for (int i = 0; i < inFrameCount; i++)
            {
                int frameOffset = i * frameSize;
                float l = 0f;
                float r = 0f;

                if (isFloat && bytesPerSample == 4)
                {
                    l = BitConverter.ToSingle(buffer, frameOffset);
                    r = channels > 1 ? BitConverter.ToSingle(buffer, frameOffset + 4) : l;
                }
                else if (bytesPerSample == 2) // 16-bit PCM
                {
                    short sl = BitConverter.ToInt16(buffer, frameOffset);
                    short sr = channels > 1 ? BitConverter.ToInt16(buffer, frameOffset + 2) : sl;
                    l = sl / 32768.0f;
                    r = sr / 32768.0f;
                }
                else if (bytesPerSample == 3) // 24-bit PCM
                {
                    int s24L = (buffer[frameOffset + 2] << 24) | (buffer[frameOffset + 1] << 16) | (buffer[frameOffset] << 8);
                    int s24R = channels > 1
                        ? (buffer[frameOffset + 5] << 24) | (buffer[frameOffset + 4] << 16) | (buffer[frameOffset + 3] << 8)
                        : s24L;
                    l = s24L / 2147483648.0f;
                    r = s24R / 2147483648.0f;
                }
                else if (bytesPerSample == 4) // 32-bit integer PCM
                {
                    int sl = BitConverter.ToInt32(buffer, frameOffset);
                    int sr = channels > 1 ? BitConverter.ToInt32(buffer, frameOffset + 4) : sl;
                    l = sl / 2147483648.0f;
                    r = sr / 2147483648.0f;
                }

                intermediate[i * 2] = l;
                intermediate[i * 2 + 1] = r;
            }

            // Resample to 48000 Hz if necessary
            short[] outPcm;
            if (sampleRate == 48000)
            {
                outPcm = new short[inFrameCount * 2];
                for (int i = 0; i < inFrameCount * 2; i++)
                {
                    outPcm[i] = (short)Math.Clamp((int)(intermediate[i] * 32767f), -32768, 32767);
                }
            }
            else
            {
                int outFrameCount = (int)Math.Round((double)inFrameCount * 48000.0 / sampleRate);
                if (outFrameCount <= 0) return;
                outPcm = new short[outFrameCount * 2];

                double step = (double)(inFrameCount - 1) / Math.Max(1, outFrameCount - 1);
                for (int i = 0; i < outFrameCount; i++)
                {
                    double srcIdx = i * step;
                    int idxFloor = (int)srcIdx;
                    int idxCeil = Math.Min(idxFloor + 1, inFrameCount - 1);
                    float frac = (float)(srcIdx - idxFloor);

                    float l = intermediate[idxFloor * 2] * (1f - frac) + intermediate[idxCeil * 2] * frac;
                    float r = intermediate[idxFloor * 2 + 1] * (1f - frac) + intermediate[idxCeil * 2 + 1] * frac;

                    outPcm[i * 2] = (short)Math.Clamp((int)(l * 32767f), -32768, 32767);
                    outPcm[i * 2 + 1] = (short)Math.Clamp((int)(r * 32767f), -32768, 32767);
                }
            }

            // Push into mixer FIFO
            MainWindow.PushAudioPacket(_sourceName, outPcm);

            // Compute peak audio levels for UI meters
            int maxL = 0;
            int maxR = 0;
            for (int i = 0; i < outPcm.Length; i += 2)
            {
                int l = Math.Abs((int)outPcm[i]);
                int r = Math.Abs((int)outPcm[i + 1]);
                if (l > maxL) maxL = l;
                if (r > maxR) maxR = r;
            }

            double volL = (maxL / 32768.0) * 100.0;
            double volR = (maxR / 32768.0) * 100.0;
            OnAudioLevelArrived?.Invoke(volL, volR);
        }

        public static System.Collections.Generic.List<RenderDeviceInfo> GetRenderDevices()
        {
            var list = new System.Collections.Generic.List<RenderDeviceInfo>();
            list.Add(new RenderDeviceInfo { DeviceId = "", Name = "Default Audio Output" });

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var ep in endpoints)
                {
                    string name = ep.FriendlyName;
                    if (name.IndexOf("DeckLink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Blackmagic", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    list.Add(new RenderDeviceInfo { DeviceId = ep.ID, Name = name });
                }
            }
            catch { }

            return list;
        }

        public void Dispose()
        {
            _isDisposed = true;
            Stop();
        }
    }

    public class RenderDeviceInfo
    {
        public string DeviceId { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
