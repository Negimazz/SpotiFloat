// File: Services/AudioVisualizerService.cs
// What it does: Captures system playback audio and converts it into visualizer band levels.
// Why it exists: Drives the menu visualizer from real audio instead of random animation.
// RELATED FILES: MainWindow.xaml.cs, MainWindow.xaml, Services/SpotifyPlaybackService.cs

using NAudio.Dsp;
using NAudio.Wave;

namespace SpotiFloat.Services;

public sealed class AudioVisualizerService : IDisposable
{
    private const int FftSize = 1024;
    private readonly object syncRoot = new();
    private readonly float[] samples = new float[FftSize];
    private WasapiLoopbackCapture? capture;
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
        var fft = new Complex[FftSize];
        lock (syncRoot)
        {
            for (var i = 0; i < FftSize; i++)
            {
                var index = (writeIndex + i) % FftSize;
                var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                fft[i].X = (float)(samples[index] * window);
                fft[i].Y = 0;
            }
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), fft);

        var bands = new double[count];
        var usableBins = FftSize / 2;
        var total = 0.0;
        for (var band = 0; band < count; band++)
        {
            var start = GetLogBin(band, count, usableBins);
            var end = Math.Max(start + 1, GetLogBin(band + 1, count, usableBins));
            var sum = 0.0;
            var peak = 0.0;

            for (var i = start; i < end; i++)
            {
                var magnitude = Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
                sum += magnitude;
                peak = Math.Max(peak, magnitude);
            }

            var average = sum / Math.Max(end - start, 1);
            var level = peak * 0.65 + average * 0.35;
            total += level;
            bands[band] = Math.Clamp(Math.Pow(Math.Log10(1 + level * 520), 0.62), 0, 1);
        }

        if (total < 0.006)
        {
            Array.Fill(bands, 0);
        }

        return bands;
    }

    private static int GetLogBin(int band, int count, int usableBins)
    {
        var min = Math.Log(2);
        var max = Math.Log(usableBins);
        var value = Math.Exp(min + (max - min) * band / count);
        return Math.Clamp((int)Math.Round(value), 1, usableBins - 1);
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
