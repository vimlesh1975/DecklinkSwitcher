using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DecklinkSwitcher
{
    /// <summary>
    /// Reads Chromium OSR D3D11 shared textures (from OnAcceleratedPaint) into a CPU BGRA buffer
    /// using a reusable staging texture, avoiding Viz CopyOutputRequest IPC readback stalls.
    /// </summary>
    internal sealed class D3D11SharedTextureReader : IDisposable
    {
        private readonly object _lock = new object();
        private ID3D11Device? _device;
        private ID3D11Device1? _device1;
        private ID3D11DeviceContext? _context;
        private ID3D11Texture2D? _staging;
        private int _stagingW;
        private int _stagingH;
        private Format _stagingFmt;
        private bool _disposed;

        private bool EnsureDevice()
        {
            if (_device != null) return true;

            try
            {
                D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                    out _device,
                    out _context);

                _device1 = _device?.QueryInterfaceOrNull<ID3D11Device1>();
                return _device != null && _context != null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[D3D11SharedTextureReader] CreateDevice failed: {ex.Message}");
                DisposeDeviceOnly();
                return false;
            }
        }

        private void EnsureStaging(int width, int height, Format format)
        {
            if (_staging != null && _stagingW == width && _stagingH == height && _stagingFmt == format)
                return;

            _staging?.Dispose();
            _staging = null;

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            _staging = _device!.CreateTexture2D(desc);
            _stagingW = width;
            _stagingH = height;
            _stagingFmt = format;
        }

        public unsafe bool TryCopySharedTexture(IntPtr sharedHandle, int expectedWidth, int expectedHeight, byte[] dstBgra)
        {
            if (_disposed || sharedHandle == IntPtr.Zero) return false;

            lock (_lock)
            {
                if (_disposed || !EnsureDevice()) return false;

                ID3D11Texture2D? sharedTex = null;
                try
                {
                    // Chromium 126+ uses NT handles (OpenSharedResource1); fall back to legacy OpenSharedResource if needed.
                    if (_device1 != null)
                    {
                        try
                        {
                            sharedTex = _device1.OpenSharedResource1<ID3D11Texture2D>(sharedHandle);
                        }
                        catch
                        {
                            sharedTex = null;
                        }
                    }

                    if (sharedTex == null)
                    {
                        sharedTex = _device!.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
                    }

                    if (sharedTex == null) return false;

                    var texDesc = sharedTex.Description;
                    int w = (int)texDesc.Width;
                    int h = (int)texDesc.Height;
                    if (w != expectedWidth || h != expectedHeight) return false;

                    EnsureStaging(w, h, texDesc.Format);
                    if (_staging == null) return false;

                    _context!.CopyResource(_staging, sharedTex);

                    var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        int rowBytes = w * 4;
                        byte* srcBase = (byte*)mapped.DataPointer;
                        int srcPitch = (int)mapped.RowPitch;
                        fixed (byte* dstBase = dstBgra)
                        {
                            if (srcPitch == rowBytes)
                            {
                                Buffer.MemoryCopy(srcBase, dstBase, (long)rowBytes * h, (long)rowBytes * h);
                            }
                            else
                            {
                                for (int y = 0; y < h; y++)
                                {
                                    Buffer.MemoryCopy(srcBase + y * srcPitch, dstBase + y * rowBytes, rowBytes, rowBytes);
                                }
                            }
                        }
                    }
                    finally
                    {
                        _context.Unmap(_staging, 0);
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    sharedTex?.Dispose();
                }
            }
        }

        private void DisposeDeviceOnly()
        {
            try { _staging?.Dispose(); } catch { }
            _staging = null;
            try { _device1?.Dispose(); } catch { }
            _device1 = null;
            try { _context?.Dispose(); } catch { }
            _context = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                DisposeDeviceOnly();
            }
        }
    }
}

