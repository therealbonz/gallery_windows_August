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

        private const int FftSize = 512;
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
        private float _peakTracker = 0.05f;

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

            try
            {
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
            catch (Exception ex)
            {
                AppLogger.Log($"OnDataAvailable error: {ex.Message}");
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

            // Compute magnitudes for first FftSize / 2 bins (256 bins)
            int half = FftSize / 2; // 256 bins
            float[] mag = new float[half];
            float maxMag = 0.0001f;
            for (int i = 0; i < half; i++)
            {
                mag[i] = (float)Math.Sqrt(_real[i] * _real[i] + _imag[i] * _imag[i]) / half;
                if (mag[i] > maxMag) maxMag = mag[i];
            }

            // Automatic Gain Control (AGC): dynamically adapts so quiet/normal YouTube volume triggers full visualizer dance
            if (maxMag > _peakTracker)
                _peakTracker = _peakTracker * 0.6f + maxMag * 0.4f;
            else
                _peakTracker = Math.Max(0.005f, _peakTracker * 0.992f);

            float dynamicGain = Math.Clamp(1.0f / _peakTracker, 4.0f, 65.0f);

            // 1. Sub-bass & Kick: Bins 0, 1, 2, 3 (~0 - 375 Hz at 48kHz)
            float rawBass = (mag[0] * 1.5f + mag[1] * 2.0f + mag[2] * 1.8f + mag[3] * 1.2f) * dynamicGain * 0.45f;
            rawBass = Math.Clamp(rawBass, 0f, 1f);

            // 2. Mids: Bins 4 to 24 (~375 - 2,250 Hz)
            float sumMid = 0f;
            for (int i = 4; i <= 24; i++) sumMid += mag[i];
            float rawMid = (sumMid / 21f) * dynamicGain * 1.8f;
            rawMid = Math.Clamp(rawMid, 0f, 1f);

            // 3. Treble: Bins 25 to 80 (~2,250 - 7,500 Hz)
            float sumTreble = 0f;
            for (int i = 25; i <= 80; i++) sumTreble += mag[i];
            float rawTreble = (sumTreble / 56f) * dynamicGain * 2.8f;
            rawTreble = Math.Clamp(rawTreble, 0f, 1f);

            // Smooth response with punchy attack
            _bass = Math.Max(rawBass, _bass * 0.78f);
            _mid = rawMid;
            _treble = rawTreble;

            // 4. 32 Equalizer Bands across first 64 bins (2 bins each)
            for (int b = 0; b < 32; b++)
            {
                float sum = (mag[b * 2] + mag[b * 2 + 1]) * 0.5f;
                float tiltBoost = 1.0f + (b / 32.0f) * 2.2f; // High frequency tilt
                float val = Math.Clamp(sum * dynamicGain * tiltBoost * 1.1f, 0f, 1f);
                _bands[b] = Math.Max(val, _bands[b] * 0.74f);
            }

            // Throttle dispatch to ~35 FPS (~28 ms)
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
