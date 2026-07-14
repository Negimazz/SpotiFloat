// File: Services/AudioVisualizerService.cs
// What it does: Captures system playback audio and converts it into visualizer band levels.
// Why it exists: Drives the menu visualizer from real audio instead of random animation.
// RELATED FILES: MainWindow.xaml.cs, MainWindow.xaml, Services/SpotifyPlaybackService.cs

using NAudio.Dsp;
using NAudio.Wave;

namespace SpotiFloat.Services;

public sealed class AudioVisualizerService : IDisposable
{
    private const int FftSize = 2048;
    private const double MinimumFrequency = 35;
    private const double MaximumFrequency = 16000;
    private readonly object syncRoot = new();
    private readonly float[] samples = new float[FftSize];
    private WasapiLoopbackCapture? capture;
    private double[] smoothedBands = Array.Empty<double>();
    private int sampleRate = 48000;
    private int writeIndex;

    public void Start()
    {
        if (capture is not null)
        {
            return;
        }

        try
        {
            capture = new WasapiLoopbackCapture();
            sampleRate = capture.WaveFormat.SampleRate;
            capture.DataAvailable += Capture_DataAvailable;
            capture.RecordingStopped += (_, _) => DisposeCapture();
            capture.StartRecording();
        }
        catch
        {
            DisposeCapture();
        }
    }

    public double[] GetBands(int count)
    {
        if (count <= 0)
        {
            return Array.Empty<double>();
        }

        var fft = new Complex[FftSize];
        var signalPower = 0.0;
        lock (syncRoot)
        {
            for (var i = 0; i < FftSize; i++)
            {
                var index = (writeIndex + i) % FftSize;
                var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                fft[i].X = (float)(samples[index] * window);
                fft[i].Y = 0;
                signalPower += samples[index] * samples[index];
            }
        }

        EnsureSmoothingBuffer(count);
        if (Math.Sqrt(signalPower / FftSize) < 0.0008)
        {
            Array.Fill(smoothedBands, 0);
            return new double[count];
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), fft);

        var bands = new double[count];
        var usableBins = FftSize / 2;
        var highestFrequency = Math.Min(MaximumFrequency, sampleRate / 2.0);
        for (var band = 0; band < count; band++)
        {
            var start = GetLogBin(band, count, usableBins, highestFrequency);
            var end = Math.Max(start + 1, GetLogBin(band + 1, count, usableBins, highestFrequency));
            var sumSquares = 0.0;
            var peak = 0.0;

            for (var i = start; i < end; i++)
            {
                var magnitude = Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
                sumSquares += magnitude * magnitude;
                peak = Math.Max(peak, magnitude);
            }

            var rms = Math.Sqrt(sumSquares / Math.Max(end - start, 1));
            var frequencyCompensation = 1 + 1.7 * band / Math.Max(count - 1.0, 1);
            var level = (peak * 0.58 + rms * 0.42) * frequencyCompensation;
            var target = Math.Clamp(Math.Pow(Math.Log10(1 + level * 620), 0.68), 0, 1);

            var smoothing = target > smoothedBands[band] ? 0.72 : 0.28;
            smoothedBands[band] += (target - smoothedBands[band]) * smoothing;
            bands[band] = smoothedBands[band];
        }

        return bands;
    }

    private int GetLogBin(int band, int count, int usableBins, double highestFrequency)
    {
        var frequency = MinimumFrequency * Math.Pow(highestFrequency / MinimumFrequency, band / (double)count);
        var bin = frequency * FftSize / sampleRate;
        return Math.Clamp((int)Math.Round(bin), 1, usableBins - 1);
    }

    private void EnsureSmoothingBuffer(int count)
    {
        if (smoothedBands.Length != count)
        {
            smoothedBands = new double[count];
        }
    }

    public void Dispose()
    {
        DisposeCapture();
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (capture is null)
        {
            return;
        }

        var channels = capture.WaveFormat.Channels;
        var bytesPerSample = capture.WaveFormat.BitsPerSample / 8;
        if (bytesPerSample != 4)
        {
            return;
        }

        lock (syncRoot)
        {
            for (var offset = 0; offset + bytesPerSample * channels <= e.BytesRecorded; offset += bytesPerSample * channels)
            {
                var mixed = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    mixed += BitConverter.ToSingle(e.Buffer, offset + channel * bytesPerSample);
                }

                samples[writeIndex] = mixed / channels;
                writeIndex = (writeIndex + 1) % FftSize;
            }
        }
    }

    private void DisposeCapture()
    {
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= Capture_DataAvailable;
        capture.Dispose();
        capture = null;
    }
}
