using System;
using System.Diagnostics;
using NAudio.Wave;

namespace My3DCubeWallpaper
{
    public class SystemAudioCapture : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private bool _isRunning = false;
        private readonly object _lock = new();

        private const int FftSize = 256;
        private readonly float[] _sampleBuffer = new float[FftSize];
        private int _sampleCount = 0;

        private readonly float[] _real = new float[FftSize];
        private readonly float[] _imag = new float[FftSize];
        private readonly float[] _window = new float[FftSize];

        // 32 Frequency bands
        private readonly float[] _bands = new float[32];
        private float _bass = 0f;
        private float _mid = 0f;
        private float _treble = 0f;

        private long _lastDispatchTicks = 0;

        public event Action<float[], float, float, float>? AudioDataAvailable;

        public bool IsRunning => _isRunning;

        public SystemAudioCapture()
        {
            // Precalculate Hann window
            for (int i = 0; i < FftSize; i++)
            {
                _window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            }
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning) return;
                try
                {
                    _capture = new WasapiLoopbackCapture();
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += (s, e) =>
                    {
                        _isRunning = false;
                        AppLogger.Log("WasapiLoopbackCapture recording stopped.");
                    };

                    _capture.StartRecording();
                    _isRunning = true;
                    AppLogger.Log($"SystemAudioCapture started. WaveFormat: {_capture.WaveFormat}");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"SystemAudioCapture start failed: {ex.Message}");
                    _isRunning = false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning) return;
                _isRunning = false;
                try
                {
                    _capture?.StopRecording();
                    _capture?.Dispose();
                    _capture = null;
                    AppLogger.Log("SystemAudioCapture stopped.");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"SystemAudioCapture stop error: {ex.Message}");
                }
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isRunning || _capture == null) return;

            var waveFormat = _capture.WaveFormat;
            int channels = waveFormat.Channels;
            bool isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
            int bytesPerSample = waveFormat.BitsPerSample / 8;
            int frameSize = channels * bytesPerSample;

            int totalFrames = e.BytesRecorded / frameSize;
            if (totalFrames <= 0) return;

            for (int f = 0; f < totalFrames; f++)
            {
                int offset = f * frameSize;
                float mono = 0f;

                if (isFloat && bytesPerSample == 4)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        mono += BitConverter.ToSingle(e.Buffer, offset + ch * 4);
                    }
                    mono /= channels;
                }
                else if (bytesPerSample == 2)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        short s = BitConverter.ToInt16(e.Buffer, offset + ch * 2);
                        mono += s / 32768f;
                    }
                    mono /= channels;
                }

                _sampleBuffer[_sampleCount++] = mono;

                if (_sampleCount >= FftSize)
                {
                    _sampleCount = 0;
                    ProcessFft();
                }
            }
        }

        private void ProcessFft()
        {
            for (int i = 0; i < FftSize; i++)
            {
                _real[i] = _sampleBuffer[i] * _window[i];
                _imag[i] = 0f;
            }

            ComputeFft(_real, _imag);

            // Compute magnitudes for first FftSize / 2 bins
            int half = FftSize / 2; // 128 bins
            float[] mag = new float[half];
            for (int i = 0; i < half; i++)
            {
                mag[i] = (float)Math.Sqrt(_real[i] * _real[i] + _imag[i] * _imag[i]) / half;
            }

            // 1. Sub-bass & Kick: bins 1 to 4 (~180 - 750 Hz)
            float rawBass = 0f;
            for (int i = 1; i <= 4; i++) rawBass += mag[i];
            rawBass = Math.Clamp(rawBass * 1.8f, 0f, 1f);

            // 2. Mids: bins 5 to 16 (~750 - 3000 Hz)
            float rawMid = 0f;
            for (int i = 5; i <= 16; i++) rawMid += mag[i];
            rawMid = Math.Clamp(rawMid * 2.2f, 0f, 1f);

            // 3. Treble: bins 17 to 48 (~3000 - 9000 Hz)
            float rawTreble = 0f;
            for (int i = 17; i <= 48; i++) rawTreble += mag[i];
            rawTreble = Math.Clamp(rawTreble * 3.5f, 0f, 1f);

            _bass = Math.Max(rawBass, _bass * 0.82f);
            _mid = rawMid;
            _treble = rawTreble;

            // 4. 32 Equalizer Bands across first 64 bins (2 bins each)
            for (int b = 0; b < 32; b++)
            {
                float sum = mag[b * 2] + mag[b * 2 + 1];
                float boost = 1.0f + (b / 32.0f) * 1.8f; // high frequency tilt boost
                float val = Math.Clamp(sum * 2.4f * boost, 0f, 1f);
                _bands[b] = Math.Max(val, _bands[b] * 0.78f);
            }

            // Throttle notification to ~35 FPS (~28 ms)
            long now = Environment.TickCount64;
            if (now - _lastDispatchTicks >= 28)
            {
                _lastDispatchTicks = now;
                AudioDataAvailable?.Invoke(_bands, _bass, _mid, _treble);
            }
        }

        // Cooley-Tukey Radix-2 In-Place Decimation-In-Time FFT
        private static void ComputeFft(float[] real, float[] imag)
        {
            int n = real.Length;

            // Bit-reversal permutation
            int j = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (i < j)
                {
                    float tr = real[i]; real[i] = real[j]; real[j] = tr;
                    float ti = imag[i]; imag[i] = imag[j]; imag[j] = ti;
                }
                int k = n / 2;
                while (k <= j)
                {
                    j -= k;
                    k /= 2;
                }
                j += k;
            }

            // Butterfly computation
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2.0 * Math.PI / len;
                float wlenReal = (float)Math.Cos(angle);
                float wlenImag = (float)Math.Sin(angle);

                for (int i = 0; i < n; i += len)
                {
                    float wReal = 1.0f;
                    float wImag = 0.0f;

                    for (int k = 0; k < len / 2; k++)
                    {
                        int u = i + k;
                        int v = i + k + len / 2;

                        float vReal = real[v] * wReal - imag[v] * wImag;
                        float vImag = real[v] * wImag + imag[v] * wReal;

                        real[v] = real[u] - vReal;
                        imag[v] = imag[u] - vImag;
                        real[u] += vReal;
                        imag[u] += vImag;

                        float nextWReal = wReal * wlenReal - wImag * wlenImag;
                        wImag = wReal * wlenImag + wImag * wlenReal;
                        wReal = nextWReal;
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
