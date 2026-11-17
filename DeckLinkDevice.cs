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

        private _BMDDisplayMode _currentDisplayMode = _BMDDisplayMode.bmdModeHD1080i50;
        public _BMDDisplayMode CurrentDisplayMode => _currentDisplayMode;

        private static readonly int[] _palTo1080XMap = BuildPalTo1080XMap();

        private static int[] BuildPalTo1080XMap()
        {
            int[] map = new int[960];
            for (int mp = 0; mp < 960; mp++)
            {
                int x = mp * 2;
                int srcX = (int)((long)x * 720 / 1920);
                if (srcX > 718) srcX = 718;
                map[mp] = (srcX / 2) * 4;
            }
            return map;
        }

        public static unsafe void ScalePalUyvyTo1080Uyvy(byte* src, int srcRowBytes, byte* dest, int destRowBytes)
        {
            Parallel.For(0, 1080, y =>
            {
                int srcY = (y * 576) / 1080;
                byte* srcLine = src + (srcY * srcRowBytes);
                byte* destLine = dest + (y * destRowBytes);

                fixed (int* xMap = _palTo1080XMap)
                {
                    uint* dest32 = (uint*)destLine;
                    for (int mp = 0; mp < 960; mp++)
                    {
                        dest32[mp] = *(uint*)(srcLine + xMap[mp]);
                    }
                }
            });
        }

        public static unsafe void ScaleUyvyTo1080Uyvy(byte* src, int srcW, int srcH, int srcRowBytes, byte* dest, int destRowBytes)
        {
            if (srcW == 720 && srcH == 576)
            {
                ScalePalUyvyTo1080Uyvy(src, srcRowBytes, dest, destRowBytes);
                return;
            }

            Parallel.For(0, 1080, y =>
            {
                int srcY = (y * srcH) / 1080;
                if (srcY >= srcH) srcY = srcH - 1;
                byte* srcLine = src + (srcY * srcRowBytes);
                byte* destLine = dest + (y * destRowBytes);

                uint* dest32 = (uint*)destLine;
                for (int mp = 0; mp < 960; mp++)
                {
                    int x = mp * 2;
                    int srcX = (int)((long)x * srcW / 1920);
                    if (srcX >= srcW - 1) srcX = srcW - 2;
                    dest32[mp] = *(uint*)(srcLine + (srcX / 2) * 4);
                }
            });
        }

        public void StartCapture(_BMDDisplayMode displayMode = _BMDDisplayMode.bmdModeHD1080i50, bool enableFormatDetection = true)
        {
            if (_isCapturing) return;

            _currentDisplayMode = displayMode;
            MainWindow.Log($"[{_roleName}] Starting capture with mode: {displayMode}, autoDetect: {enableFormatDetection}");
            try
            {
                _deckLinkInput.SetCallback(this);

                var flags = enableFormatDetection
                    ? _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection
                    : _BMDVideoInputFlags.bmdVideoInputFlagDefault;

                try
                {
                    _deckLinkInput.EnableVideoInput(displayMode, _BMDPixelFormat.bmdFormat8BitYUV, flags);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[{_roleName}] EnableVideoInput with flags {flags} failed: {ex.Message}. Falling back to default flag...");
                    _deckLinkInput.EnableVideoInput(displayMode, _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoInputFlags.bmdVideoInputFlagDefault);
                }

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
                _deckLinkOutput.CreateVideoFrame(1920, 1080, 1920 * 2, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out _reusableOutputFrame);
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

                    if (width == 1920 && height == 1080)
                    {
                        UIntPtr size = new UIntPtr(1920 * 1080 * 2);
                        memcpy(outputBuffer, inputBuffer, size);
                    }
                    else
                    {
                        unsafe
                        {
                            ScaleUyvyTo1080Uyvy((byte*)inputBuffer, width, height, rowBytes, (byte*)outputBuffer, 1920 * 2);
                        }
                    }
                    unsafe
                    {
                        MainWindow.ApplyLogoOverlayUyvy((byte*)outputBuffer);
                    }
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
                    if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording || MainWindow.NdiOut.IsRunning || MainWindow.SrtOut.IsStreaming || MainWindow.IsFullscreenPreviewActive)
                    {
                        int h = _reusableOutputFrame.GetHeight();
                        int outRowBytes = _reusableOutputFrame.GetRowBytes();
                        uyvyBytes = new byte[outRowBytes * h];
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
                _deckLinkOutput.CreateVideoFrame(1920, 1080, 1920 * 2, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out _reusableOutputFrame);
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
                if (width == 1920 && height == 1080)
                {
                    UIntPtr size = new UIntPtr(1920 * 1080 * 2);
                    memcpy(outputBuffer, inputBuffer, size);
                }
                else
                {
                    unsafe
                    {
                        ScaleUyvyTo1080Uyvy((byte*)inputBuffer, width, height, rowBytes, (byte*)outputBuffer, 1920 * 2);
                    }
                }
                unsafe
                {
                    MainWindow.ApplyLogoOverlayUyvy((byte*)outputBuffer);
                }
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
                    if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording || MainWindow.NdiOut.IsRunning || MainWindow.SrtOut.IsStreaming || MainWindow.IsFullscreenPreviewActive)
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
        public volatile bool IsCompositeInput = false;
        private byte[]? _latestCompositeUyvy1080;

        public byte[]? GetLatestCompositeUyvy1080() => _latestCompositeUyvy1080;

        public void ScheduleSyntheticFrame(int sourceType)
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
                    if (sourceType == 2) // Matte
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
                    else if (sourceType == 1) // Color Bars
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
                    else if (sourceType == 6) // YouTube direct 1080p UYVY
                    {
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow.LatestYouTubeUyvy != null)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(MainWindow.LatestYouTubeUyvy, 0, outputBuffer, MainWindow.LatestYouTubeUyvy.Length);
                            }
                        }
                    }
                    else if (sourceType == 8) // NDI source direct 1080p UYVY
                    {
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow.LatestNdiUyvy != null)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(MainWindow.LatestNdiUyvy, 0, outputBuffer, MainWindow.LatestNdiUyvy.Length);
                            }
                        }
                    }
                    else if (sourceType == 7) // HTML / Web direct 1080p UYVY
                    {
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow.LatestHtmlSourceUyvy != null)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(MainWindow.LatestHtmlSourceUyvy, 0, outputBuffer, MainWindow.LatestHtmlSourceUyvy.Length);
                            }
                        }
                    }
                    else if (sourceType == 9) // Multi-Box / Composite 1080p UYVY
                    {
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow.LatestCompositeUyvy != null)
                            {
                                System.Runtime.InteropServices.Marshal.Copy(MainWindow.LatestCompositeUyvy, 0, outputBuffer, MainWindow.LatestCompositeUyvy.Length);
                            }
                        }
                    }
                    else if (sourceType == 4 || sourceType == 5) // Desktop Capture or Webcam
                    {
                        byte[] deskUyvy = null;
                        lock (MainWindow._pgmLock)
                        {
                            if (MainWindow._latestPgmFrame != null)
                            {
                                if (MainWindow._latestPgmFrame.Length == 1920 * 1080 * 4)
                                    deskUyvy = MainWindow.ConvertBgra1080ToUyvy1080(MainWindow._latestPgmFrame);
                                else
                                    deskUyvy = MainWindow.ConvertBgraToUyvy1080(MainWindow._latestPgmFrame);
                            }
                        }
                        if (deskUyvy != null)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(deskUyvy, 0, outputBuffer, deskUyvy.Length);
                        }
                    }

                    MainWindow.ApplyLogoOverlayUyvy(ptr);
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
                if (MainWindow.YtStreamer.IsStreaming || MainWindow.LclRecorder.IsRecording || MainWindow.NdiOut.IsRunning || MainWindow.SrtOut.IsStreaming || MainWindow.IsFullscreenPreviewActive)
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
            try
            {
                if (newDisplayMode == null) return;

                _BMDDisplayMode mode = newDisplayMode.GetDisplayMode();
                newDisplayMode.GetName(out string modeName);
                MainWindow.Log($"[{_roleName}] Video input format change detected: {modeName} ({mode})");

                if ((notificationEvents & _BMDVideoInputFormatChangedEvents.bmdVideoInputDisplayModeChanged) != 0 && mode != _currentDisplayMode)
                {
                    MainWindow.Log($"[{_roleName}] Renegotiating input stream to {modeName}...");
                    _deckLinkInput.PauseStreams();
                    _deckLinkInput.EnableVideoInput(mode, _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection);
                    _deckLinkInput.FlushStreams();
                    _deckLinkInput.StartStreams();
                    _currentDisplayMode = mode;
                    MainWindow.Log($"[{_roleName}] Successfully updated stream format to {modeName}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[{_roleName}] VideoInputFormatChanged error: {ex.Message}");
            }
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
                    bool captureForComposite = IsCompositeInput;
                    if (IsProgramSource || captureForComposite) shouldUpdatePreview = true;
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

                            if (captureForComposite)
                            {
                                _latestCompositeUyvy1080 ??= new byte[1920 * 1080 * 2];
                                unsafe
                                {
                                    fixed (byte* compPtr = _latestCompositeUyvy1080)
                                    {
                                        if (width == 1920 && height == 1080 && srcRowBytes == 1920 * 2)
                                        {
                                            Buffer.MemoryCopy((void*)inputBuffer, compPtr, 1920 * 1080 * 2, 1920 * 1080 * 2);
                                        }
                                        else
                                        {
                                            ScaleUyvyTo1080Uyvy((byte*)inputBuffer, width, height, srcRowBytes, compPtr, 1920 * 2);
                                        }
                                    }
                                }
                            }
                            
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
                        if (MainWindow.InvertDeckLinkRight)
                        {
                            for (int i = 1; i < arr.Length; i += 2)
                                arr[i] = arr[i] == short.MinValue ? short.MaxValue : (short)(-arr[i]);
                        }
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
}
