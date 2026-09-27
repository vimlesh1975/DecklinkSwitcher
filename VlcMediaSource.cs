using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace DecklinkSwitcher
{
    public class VlcMediaSource : IDisposable
    {
        private LibVLC _libVlc;
        private MediaPlayer _mediaPlayer;
        private IntPtr _videoBuffer;
        private int _width = 1920;
        private int _height = 1080;
        
        private short[] _audioBuffer = new short[48000 * 2]; // Max 1 sec buffer
        private int _audioWriteIdx = 0;
        private int _audioReadIdx = 0;
        private object _audioLock = new object();

        // Delegate matches ScheduleCustomFrame inside DeckLinkDevice
        public Action<IntPtr, int, int, int, IntPtr, uint> OnVideoAndAudioArrived;
        public Action<byte[]> OnPreviewBufferUpdated;
        public Action<double, double> OnAudioLevelArrived;
        
        public float AudioLevel { get; set; } = 1.0f;

        private IntPtr _previewBuffer;
        private IntPtr _audioFormatPtr;

        private LibVLCSharp.Shared.MediaPlayer.LibVLCVideoLockCb _lockVideoCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCVideoUnlockCb _unlockVideoCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCVideoDisplayCb _displayVideoCb;

        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPlayCb _playAudioCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPauseCb _pauseAudioCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioResumeCb _resumeAudioCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioFlushCb _flushAudioCb;
        private LibVLCSharp.Shared.MediaPlayer.LibVLCAudioDrainCb _drainAudioCb;

        public VlcMediaSource()
        {
            _videoBuffer = Marshal.AllocCoTaskMem(_width * _height * 2); // UYVY
            _previewBuffer = Marshal.AllocCoTaskMem(480 * 270 * 4); // BGRA

            Core.Initialize();
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            
            _audioFormatPtr = Marshal.AllocCoTaskMem(4);
            byte[] fmt = System.Text.Encoding.ASCII.GetBytes("S16N");
            Marshal.Copy(fmt, 0, _audioFormatPtr, 4);
            
            _mediaPlayer.SetVideoFormat("UYVY", (uint)_width, (uint)_height, (uint)(_width * 2));
            
            _lockVideoCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCVideoLockCb(LockVideo);
            _unlockVideoCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCVideoUnlockCb(UnlockVideo);
            _displayVideoCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCVideoDisplayCb(DisplayVideo);
            _mediaPlayer.SetVideoCallbacks(_lockVideoCb, _unlockVideoCb, _displayVideoCb);
            
            _mediaPlayer.SetAudioFormat("S16N", 48000, 2);
            
            _playAudioCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPlayCb(PlayAudio);
            _pauseAudioCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPauseCb(PauseAudio);
            _resumeAudioCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCAudioResumeCb(ResumeAudio);
            _flushAudioCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCAudioFlushCb(FlushAudio);
            _drainAudioCb = new LibVLCSharp.Shared.MediaPlayer.LibVLCAudioDrainCb(DrainAudio);
            _mediaPlayer.SetAudioCallbacks(_playAudioCb, _pauseAudioCb, _resumeAudioCb, _flushAudioCb, _drainAudioCb);
        }

        private bool _pauseOnNextFrame = false;

        public void Play(string filePath, bool loop)
        {
            _pauseOnNextFrame = false;
            var media = new Media(_libVlc, filePath, FromType.FromPath);
            if (loop) media.AddOption(":input-repeat=65535"); // Loop indefinitely
            
            string lowerPath = filePath.ToLower();
            if (lowerPath.EndsWith(".jpg") || lowerPath.EndsWith(".jpeg") || lowerPath.EndsWith(".png") || lowerPath.EndsWith(".bmp"))
            {
                media.AddOption(":image-duration=-1");
            }
            
            _mediaPlayer.Play(media);
        }

        public void Cue(string filePath, bool loop)
        {
            _pauseOnNextFrame = true;
            var media = new Media(_libVlc, filePath, FromType.FromPath);
            if (loop) media.AddOption(":input-repeat=65535");
            
            string lowerPath = filePath.ToLower();
            if (lowerPath.EndsWith(".jpg") || lowerPath.EndsWith(".jpeg") || lowerPath.EndsWith(".png") || lowerPath.EndsWith(".bmp"))
            {
                media.AddOption(":image-duration=-1");
            }
            
            _mediaPlayer.Play(media);
        }
        
        public void Stop()
        {
            _mediaPlayer.Stop();
        }

        public void Pause()
        {
            _mediaPlayer.SetPause(true);
        }

        public void Resume()
        {
            _mediaPlayer.SetPause(false);
        }

        public float Position
        {
            get => _mediaPlayer.Position;
            set => _mediaPlayer.Position = value;
        }

        public long Time
        {
            get => _mediaPlayer.Time;
            set => _mediaPlayer.Time = value;
        }

        public long Length
        {
            get => _mediaPlayer.Length;
        }



        private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _videoBuffer);
            return IntPtr.Zero;
        }

        private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
        }

        private int _lastPreviewTicks = 0;

        private void DisplayVideo(IntPtr opaque, IntPtr picture)
        {
            if (_pauseOnNextFrame)
            {
                _pauseOnNextFrame = false;
                System.Threading.Tasks.Task.Run(() => {
                    _mediaPlayer.SetPause(true);
                    _mediaPlayer.Position = 0;
                });
            }

            uint sampleCount = 1920; 
            IntPtr audioPtr = Marshal.AllocCoTaskMem((int)sampleCount * 4);
            
            unsafe 
            {
                short* dst = (short*)audioPtr.ToPointer();
                lock (_audioLock)
                {
                    int available = _audioWriteIdx - _audioReadIdx;
                    if (available < 0) available += _audioBuffer.Length;
                    
                    int toRead = Math.Min((int)sampleCount * 2, available);
                    int readSamples = 0;
                    for (int i = 0; i < toRead; i++)
                    {
                        dst[i] = _audioBuffer[_audioReadIdx];
                        _audioReadIdx = (_audioReadIdx + 1) % _audioBuffer.Length;
                        readSamples++;
                    }
                    // zero out the rest
                    for (int i = readSamples; i < sampleCount * 2; i++) dst[i] = 0;
                }
                
                short[] arr = new short[sampleCount * 2];
                Marshal.Copy(audioPtr, arr, 0, (int)sampleCount * 2);
                DecklinkSwitcher.MainWindow.LatestAudioPackets["Media"] = arr;
                
                int maxL = 0;
                int maxR = 0;
                for (int i = 0; i < sampleCount * 2; i += 2)
                {
                    int l = Math.Abs(dst[i]);
                    int r = Math.Abs(dst[i + 1]);
                    if (l > maxL) maxL = l;
                    if (r > maxR) maxR = r;
                }
                double volL = (maxL / 32768.0) * 100.0;
                double volR = (maxR / 32768.0) * 100.0;
                OnAudioLevelArrived?.Invoke(volL, volR);
            }

            OnVideoAndAudioArrived?.Invoke(_videoBuffer, _width, _height, _width * 2, audioPtr, sampleCount);
            
            Marshal.FreeCoTaskMem(audioPtr);

            if (Environment.TickCount - _lastPreviewTicks > 200)
            {
                _lastPreviewTicks = Environment.TickCount;
                unsafe 
                {
                    DeckLinkDevice.ConvertUYVYToBGRA_Downsampled(_videoBuffer, _width, _height, _width * 2, _previewBuffer, 480, 270, 480 * 4);
                }
                byte[] bufferCopy = new byte[480 * 270 * 4];
                Marshal.Copy(_previewBuffer, bufferCopy, 0, bufferCopy.Length);
                OnPreviewBufferUpdated?.Invoke(bufferCopy);
            }
        }



        private void PlayAudio(IntPtr data, IntPtr samples, uint count, long pts)
        {
            unsafe
            {
                short* src = (short*)samples.ToPointer();
                lock (_audioLock)
                {
                    for (int i = 0; i < count * 2; i++)
                    {
                        _audioBuffer[_audioWriteIdx] = src[i];
                        _audioWriteIdx = (_audioWriteIdx + 1) % _audioBuffer.Length;
                    }
                }
            }
        }

        private void PauseAudio(IntPtr data, long pts) {}
        private void ResumeAudio(IntPtr data, long pts) {}
        private void FlushAudio(IntPtr data, long pts) {}
        private void DrainAudio(IntPtr data) {}

        public void Dispose()
        {
            Stop();
            _mediaPlayer.Dispose();
            _libVlc.Dispose();
            Marshal.FreeCoTaskMem(_videoBuffer);
            Marshal.FreeCoTaskMem(_previewBuffer);
            Marshal.FreeCoTaskMem(_audioFormatPtr);
        }
    }
}
