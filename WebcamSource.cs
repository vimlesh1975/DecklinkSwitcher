using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlashCap;

namespace DecklinkSwitcher
{
    public class WebcamDeviceInfo
    {
        public string Name { get; set; } = string.Empty;
        public CaptureDeviceDescriptor? Descriptor { get; set; }
        public override string ToString() => Name;
    }

    public class WebcamSource : IDisposable
    {
        private CaptureDevice? _captureDevice;
        private LocalAudioInput? _micInput;
        private readonly object _bufferLock = new object();
        private readonly SemaphoreSlim _deviceLock = new SemaphoreSlim(1, 1);

        private int _currentSessionId = 0;
        public string CurrentDeviceName { get; private set; } = string.Empty;

        private Bitmap _webcam1080Bmp;
        private Graphics _webcam1080G;
        private byte[] _webcam1080Buffer = new byte[1920 * 1080 * 4];

        private Bitmap _webcam480Bmp;
        private Graphics _webcam480G;
        private byte[] _webcam480Buffer = new byte[480 * 270 * 4];

        private int _lastRawW = 0;
        private int _lastRawH = 0;

        public Action<byte[]>? OnPreviewBufferUpdated;
        public Action<byte[]>? OnPgmBufferUpdated;
        public Action<double, double>? OnAudioLevelArrived;

        public bool IsRunning => _captureDevice != null;

        public WebcamSource()
        {
            _webcam1080Bmp = new Bitmap(1920, 1080, PixelFormat.Format32bppRgb);
            _webcam1080G = Graphics.FromImage(_webcam1080Bmp);
            _webcam1080G.InterpolationMode = InterpolationMode.Bilinear;
            _webcam1080G.SmoothingMode = SmoothingMode.None;
            _webcam1080G.PixelOffsetMode = PixelOffsetMode.None;
            _webcam1080G.Clear(Color.Black);

            _webcam480Bmp = new Bitmap(480, 270, PixelFormat.Format32bppRgb);
            _webcam480G = Graphics.FromImage(_webcam480Bmp);
            _webcam480G.InterpolationMode = InterpolationMode.Bilinear;
            _webcam480G.SmoothingMode = SmoothingMode.None;
            _webcam480G.PixelOffsetMode = PixelOffsetMode.None;
            _webcam480G.Clear(Color.Black);
        }

        public static List<WebcamDeviceInfo> GetDevices()
        {
            var list = new List<WebcamDeviceInfo>();
            try
            {
                var devices = new CaptureDevices();
                var descriptors = devices.EnumerateDescriptors()
                    .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                    .Where(d => !d.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                                !d.Name.Equals("Default Device", StringComparison.OrdinalIgnoreCase))
                    .Where(d => d.Name.IndexOf("DeckLink", StringComparison.OrdinalIgnoreCase) < 0 &&
                                d.Name.IndexOf("Blackmagic", StringComparison.OrdinalIgnoreCase) < 0 &&
                                d.Name.IndexOf("Intensity", StringComparison.OrdinalIgnoreCase) < 0 &&
                                d.Name.IndexOf("UltraStudio", StringComparison.OrdinalIgnoreCase) < 0)
                    .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(d => d.DeviceType == DeviceTypes.DirectShow ? 0 : 1).First());

                foreach (var descriptor in descriptors)
                {
                    list.Add(new WebcamDeviceInfo
                    {
                        Name = descriptor.Name,
                        Descriptor = descriptor
                    });
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[Webcam] Enumeration error: {ex.Message}");
            }
            return list;
        }

        public async Task StartAsync(CaptureDeviceDescriptor descriptor)
        {
            if (descriptor == null) return;

            await _deviceLock.WaitAsync();
            try
            {
                // If already actively running this exact device, skip redundant recreation
                if (IsRunning && CurrentDeviceName == descriptor.Name)
                {
                    return;
                }

                // Stop active device cleanly and invalidate previous session
                await StopInternalAsync();

                // Generate new session generation ID
                int mySessionId = Interlocked.Increment(ref _currentSessionId);

                // Select characteristic closest to 1080p, prioritizing standard framerate
                var chara = descriptor.Characteristics
                    .Where(c => c.Width > 0 && c.Height > 0)
                    .OrderBy(c => Math.Abs(c.Width - 1920) + Math.Abs(c.Height - 1080))
                    .ThenByDescending(c => (double)c.FramesPerSecond)
                    .FirstOrDefault();

                if (chara == null)
                {
                    MainWindow.Log($"[Webcam] No valid video characteristics found for {descriptor.Name}");
                    return;
                }

                MainWindow.Log($"[Webcam] Starting {descriptor.Name} ({chara.Width}x{chara.Height} @ {chara.FramesPerSecond.PrettyPrint} fps, format: {chara.PixelFormat})");

                _captureDevice = await descriptor.OpenAsync(chara, bufferScope =>
                {
                    // If session changed, instantly discard this frame (prevents frame mixing from old camera)
                    if (Volatile.Read(ref _currentSessionId) != mySessionId)
                    {
                        return;
                    }

                    OnFrameArrived(bufferScope, mySessionId);
                });

                await _captureDevice.StartAsync();
                CurrentDeviceName = descriptor.Name;
                MainWindow.Log($"[Webcam] Capture started successfully for {descriptor.Name}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[Webcam] StartAsync error: {ex.Message}");
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        private void OnFrameArrived(PixelBufferScope bufferScope, int session)
        {
            if (Volatile.Read(ref _currentSessionId) != session) return;

            try
            {
                byte[] imageBytes = bufferScope.Buffer.ExtractImage();
                if (imageBytes == null || imageBytes.Length == 0) return;
                if (Volatile.Read(ref _currentSessionId) != session) return;

                using var ms = new MemoryStream(imageBytes);
                using var rawBmp = new Bitmap(ms);

                int rawW = rawBmp.Width;
                int rawH = rawBmp.Height;
                if (rawW <= 0 || rawH <= 0) return;
                if (Volatile.Read(ref _currentSessionId) != session) return;

                lock (_bufferLock)
                {
                    if (Volatile.Read(ref _currentSessionId) != session) return;

                    // Re-clear background black only if frame resolution changed or on first frame of device
                    if (_lastRawW != rawW || _lastRawH != rawH)
                    {
                        _webcam1080G.Clear(Color.Black);
                        _webcam480G.Clear(Color.Black);
                        _lastRawW = rawW;
                        _lastRawH = rawH;
                    }

                    // Draw into 1080p buffer (proportional letterbox)
                    float scale1080 = Math.Min(1920f / rawW, 1080f / rawH);
                    int drawW1080 = (int)(rawW * scale1080);
                    int drawH1080 = (int)(rawH * scale1080);
                    int drawX1080 = (1920 - drawW1080) / 2;
                    int drawY1080 = (1080 - drawH1080) / 2;

                    _webcam1080G.DrawImage(rawBmp, drawX1080, drawY1080, drawW1080, drawH1080);

                    var data1080 = _webcam1080Bmp.LockBits(
                        new Rectangle(0, 0, 1920, 1080),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(data1080.Scan0, _webcam1080Buffer, 0, _webcam1080Buffer.Length);
                    _webcam1080Bmp.UnlockBits(data1080);

                    // Draw into 480x270 preview buffer
                    float scale480 = Math.Min(480f / rawW, 270f / rawH);
                    int drawW480 = (int)(rawW * scale480);
                    int drawH480 = (int)(rawH * scale480);
                    int drawX480 = (480 - drawW480) / 2;
                    int drawY480 = (270 - drawH480) / 2;

                    _webcam480G.DrawImage(rawBmp, drawX480, drawY480, drawW480, drawH480);

                    var data480 = _webcam480Bmp.LockBits(
                        new Rectangle(0, 0, 480, 270),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(data480.Scan0, _webcam480Buffer, 0, _webcam480Buffer.Length);
                    _webcam480Bmp.UnlockBits(data480);
                }

                if (Volatile.Read(ref _currentSessionId) != session) return;

                OnPreviewBufferUpdated?.Invoke(_webcam480Buffer);
                OnPgmBufferUpdated?.Invoke(_webcam1080Buffer);
            }
            catch
            {
                // Ignore transient frame decode errors during stream negotiation
            }
        }

        public byte[]? GetLatestPreviewPixels()
        {
            lock (_bufferLock)
            {
                byte[] copy = new byte[_webcam480Buffer.Length];
                Array.Copy(_webcam480Buffer, copy, copy.Length);
                return copy;
            }
        }

        public byte[]? GetLatestPgmPixels()
        {
            lock (_bufferLock)
            {
                byte[] copy = new byte[_webcam1080Buffer.Length];
                Array.Copy(_webcam1080Buffer, copy, copy.Length);
                return copy;
            }
        }

        public void StartAudio(string deviceId, string sourceName = "Webcam")
        {
            StopAudio();
            if (string.IsNullOrEmpty(deviceId) || deviceId == "None") return;

            try
            {
                _micInput = new LocalAudioInput(deviceId, sourceName);
                _micInput.OnAudioLevelArrived = (l, r) => OnAudioLevelArrived?.Invoke(l, r);
                _micInput.Start();
                MainWindow.Log($"[Webcam] Audio capturing started for device {deviceId} as {sourceName}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[Webcam] StartAudio error: {ex.Message}");
            }
        }

        public void StopAudio()
        {
            if (_micInput != null)
            {
                try
                {
                    _micInput.Stop();
                    _micInput.Dispose();
                }
                catch { }
                _micInput = null;
            }
        }

        public async Task StopAsync()
        {
            await _deviceLock.WaitAsync();
            try
            {
                await StopInternalAsync();
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        private async Task StopInternalAsync()
        {
            // Invalidate session immediately so arriving frames are discarded
            Interlocked.Increment(ref _currentSessionId);
            CurrentDeviceName = string.Empty;

            var dev = _captureDevice;
            _captureDevice = null;

            if (dev != null)
            {
                try
                {
                    await dev.StopAsync();
                }
                catch { }

                try
                {
                    if (dev is IAsyncDisposable asyncDisp)
                    {
                        await asyncDisp.DisposeAsync();
                    }
                    else
                    {
                        dev.Dispose();
                    }
                }
                catch { }
            }

            lock (_bufferLock)
            {
                _lastRawW = 0;
                _lastRawH = 0;
                _webcam1080G.Clear(Color.Black);
                Array.Clear(_webcam1080Buffer, 0, _webcam1080Buffer.Length);

                _webcam480G.Clear(Color.Black);
                Array.Clear(_webcam480Buffer, 0, _webcam480Buffer.Length);
            }

            OnPreviewBufferUpdated?.Invoke(_webcam480Buffer);
            OnPgmBufferUpdated?.Invoke(_webcam1080Buffer);
        }

        public void Dispose()
        {
            StopAudio();

            _deviceLock.Wait();
            try
            {
                StopInternalAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _deviceLock.Release();
            }

            _webcam1080G?.Dispose();
            _webcam1080Bmp?.Dispose();
            _webcam480G?.Dispose();
            _webcam480Bmp?.Dispose();
            _deviceLock?.Dispose();
        }
    }
}
