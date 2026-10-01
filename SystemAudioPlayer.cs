using NAudio.Wave;
using System;
using System.Runtime.InteropServices;

namespace DecklinkSwitcher
{
    public static class SystemAudioPlayer
    {
        private static WaveOutEvent _waveOut;
        private static BufferedWaveProvider _bufferedWaveProvider;
        private static bool _isEnabled = false;

        public static int BufferedMilliseconds => _bufferedWaveProvider == null
            ? 0
            : (int)(_bufferedWaveProvider.BufferedBytes * 1000.0 / _bufferedWaveProvider.WaveFormat.AverageBytesPerSecond);

        public static bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                if (!value && _waveOut != null)
                {
                    _bufferedWaveProvider?.ClearBuffer();
                }
            }
        }

        public static void Init()
        {
            _bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2), TimeSpan.FromMilliseconds(200));
            _bufferedWaveProvider.DiscardOnBufferOverflow = true;

            _waveOut = new WaveOutEvent();
            _waveOut.Init(_bufferedWaveProvider);
            _waveOut.Play();
        }

        private static bool _hasLogged = false;

        public static void WriteAudio(IntPtr buffer, uint sampleCount)
        {
            if (!_isEnabled || _bufferedWaveProvider == null || buffer == IntPtr.Zero || sampleCount == 0) return;

            if (!_hasLogged)
            {
                _hasLogged = true;
                System.Diagnostics.Debug.WriteLine("SystemAudioPlayer received first audio buffer.");
                System.IO.File.AppendAllText("decklinkswitcher_log.txt", "SystemAudioPlayer received first audio buffer.\n");
            }

            int byteCount = (int)sampleCount * 4; // 16-bit 2-channel
            byte[] managedArray = new byte[byteCount];
            Marshal.Copy(buffer, managedArray, 0, byteCount);
            WriteAudio(managedArray, sampleCount);
        }

        public static void WriteAudio(byte[] data, uint sampleCount)
        {
            if (!_isEnabled || _bufferedWaveProvider == null || data == null || sampleCount == 0) return;

            int byteCount = Math.Min(data.Length, checked((int)sampleCount * 4));
            _bufferedWaveProvider.AddSamples(data, 0, byteCount);
            
            if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Playing)
            {
                _waveOut.Play();
            }
        }

        public static void Shutdown()
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
        }
    }
}
