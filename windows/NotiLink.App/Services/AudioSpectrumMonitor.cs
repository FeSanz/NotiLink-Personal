using NAudio.Dsp;
using NAudio.Wave;

namespace NotiLink.App.Services;

/// <summary>
/// What this PC is playing right now, as three band levels (bass / mids / highs, 0..1) about 30
/// times a second — drives the now-playing bar's waves "al ritmo de la música" when the music
/// plays on the PC (Spotify Connect to this PC, a browser tab, the PC's own player). Captures the
/// default output device's mix with WASAPI loopback (nothing is recorded or stored; samples are
/// only FFT'd and discarded) and runs only while the UI asks for it (playing + bar visible).
/// </summary>
internal sealed class AudioSpectrumMonitor : IDisposable
{
    private const int FftSize = 1024;           // 2^10
    private const int FftM = 10;
    private const int SendEveryMs = 33;

    private WasapiLoopbackCapture? _capture;
    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private int _filled;
    private DateTime _lastSend = DateTime.MinValue;
    private readonly object _gate = new();

    /// <summary>bass, mid, treble — 0..1, roughly perceptual (dB-scaled).</summary>
    public event Action<float, float, float>? Levels;

    public bool Running { get { lock (_gate) return _capture != null; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_capture != null) return;
            try
            {
                var capture = new WasapiLoopbackCapture();
                capture.DataAvailable += OnData;
                capture.RecordingStopped += (_, _) => { lock (_gate) if (ReferenceEquals(_capture, capture)) _capture = null; };
                capture.StartRecording();
                _capture = capture;
                _filled = 0;
                PcDiagLog.Write($"audio spectrum: started ({capture.WaveFormat})");
            }
            catch (Exception ex)
            {
                PcDiagLog.Write($"audio spectrum: loopback capture failed: {ex.Message}");
                _capture = null;
            }
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? c;
        lock (_gate) { c = _capture; _capture = null; }
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.StopRecording(); c.Dispose(); } catch (Exception) { }
        PcDiagLog.Write("audio spectrum: stopped");
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = sender as WasapiLoopbackCapture;
        var fmt = capture?.WaveFormat;
        if (fmt == null || e.BytesRecorded <= 0) return;
        var channels = Math.Max(1, fmt.Channels);
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var bytesPerSample = fmt.BitsPerSample / 8;
        var frameBytes = bytesPerSample * channels;
        if (frameBytes <= 0) return;

        for (var i = 0; i + frameBytes <= e.BytesRecorded; i += frameBytes)
        {
            float mono = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                var off = i + ch * bytesPerSample;
                mono += isFloat && bytesPerSample == 4 ? BitConverter.ToSingle(e.Buffer, off)
                      : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, off) / 32768f
                      : 0f;
            }
            _ring[_ringPos] = mono / channels;
            _ringPos = (_ringPos + 1) % FftSize;
            if (_filled < FftSize) _filled++;
        }

        var now = DateTime.UtcNow;
        if (_filled < FftSize || (now - _lastSend).TotalMilliseconds < SendEveryMs) return;
        _lastSend = now;
        Analyze(fmt.SampleRate);
    }

    private void Analyze(int sampleRate)
    {
        var buf = new Complex[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            var s = _ring[(_ringPos + i) % FftSize];
            var hann = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftSize - 1)));
            buf[i].X = s * hann;
            buf[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftM, buf);

        var binHz = (float)sampleRate / FftSize;
        float Band(float lo, float hi)
        {
            int a = Math.Max(1, (int)(lo / binHz)), b = Math.Min(FftSize / 2 - 1, (int)(hi / binHz));
            if (b < a) return 0;
            double sum = 0;
            for (var k = a; k <= b; k++) sum += buf[k].X * buf[k].X + buf[k].Y * buf[k].Y;
            var rms = Math.Sqrt(sum / (b - a + 1));
            var db = 20 * Math.Log10(rms + 1e-9);
            // ~ -70 dB (silence) .. -20 dB (loud) → 0..1
            return (float)Math.Clamp((db + 70) / 50, 0, 1);
        }
        Levels?.Invoke(Band(25, 160), Band(160, 2000), Band(2000, 9000));
    }

    public void Dispose() => Stop();
}
