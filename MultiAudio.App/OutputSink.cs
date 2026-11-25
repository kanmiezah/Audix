using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiAudio.App;

/// <summary>
/// One destination: a physical output device fed by its own buffer, with live
/// volume and delay. Many sinks can be attached to a single ProcessCapture to
/// duplicate one app across several devices (spec §3.2).
/// </summary>
public sealed class OutputSink : IDisposable
{
    private readonly string _deviceId;
    private readonly BufferedWaveProvider _buffer;
    private readonly JitterBuffer _jitter;
    private readonly AdjustableDelayProvider _delay;
    private VolumeSampleProvider? _volume;
    private WasapiOut? _out;

    public string DeviceId => _deviceId;
    public string DeviceName { get; }

    // Cushion kept between capture and playback. Capture packets arrive in bursts and every
    // device (especially Bluetooth) pulls on its own schedule; without a cushion the output
    // drains the buffer between packets and plays the gap as a click.
    private const int PrimeMs = 60;
    // Backlog allowed above prime + delay before we resync. Only real clock drift between the
    // devices should get here (≈ minutes), so trims — which are audible skips — stay rare.
    private const int HeadroomMs = 120;
    private const int OutputLatencyMs = 60;

    public OutputSink(string deviceId, string deviceName)
    {
        _deviceId = deviceId;
        DeviceName = deviceName;
        _buffer = new BufferedWaveProvider(ProcessCapture.CaptureFormat)
        {
            // Must hold the max user delay (500 ms) + prime + headroom, or the delay gets eaten.
            BufferDuration = TimeSpan.FromMilliseconds(1500),
            DiscardOnBufferOverflow = true,
            ReadFully = false, // JitterBuffer decides what an underrun sounds like
        };
        _jitter = new JitterBuffer(_buffer, PrimeMs);
        _delay = new AdjustableDelayProvider(_jitter);
    }


    public void Start()
    {
        var device = new MMDeviceEnumerator().GetDevice(_deviceId);
        var mix = device.AudioClient.MixFormat;

        // Chain: 16-bit capture -> jitter buffer -> delay -> volume (float) -> match device rate/channels.
        _volume = new VolumeSampleProvider(_delay.ToSampleProvider()) { Volume = 1.0f };
        ISampleProvider s = _volume;
        // Not MediaFoundationResampler: it pulls ~1 s from its source per read, which on a live buffer
        // means seconds of padding and constant trims (the stutter/crackle). WDL pulls what's asked for.
        if (mix.SampleRate != ProcessCapture.CaptureFormat.SampleRate) s = new WdlResamplingSampleProvider(s, mix.SampleRate);
        if (mix.Channels == 1) s = new StereoToMonoSampleProvider(s);
        else if (mix.Channels > 2) s = new MultiplexingSampleProvider(new[] { s }, mix.Channels); // L/R repeat across extra channels
        IWaveProvider outStream = s.ToWaveProvider(); // 32-bit float at the device's own rate and channel count

        // 35 ms left Bluetooth endpoints too little slack in shared mode; 60 ms is still well under
        // what's noticeable for music/video and survives scheduler hiccups.
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, OutputLatencyMs);
        _out.Init(outStream);
        _out.Play();
    }

    /// <summary>Feed captured PCM bytes (in ProcessCapture.CaptureFormat) to this sink.</summary>
    public void Feed(byte[] data, int bytes)
    {
        _buffer.AddSamples(data, 0, bytes);

        // The delay provider deliberately lets DelayMs of audio pile up here, so the cap includes it.
        int targetMs = PrimeMs + DelayMs;
        if (_buffer.BufferedDuration.TotalMilliseconds > targetMs + HeadroomMs)
        {
            int keep = (int)(_buffer.WaveFormat.AverageBytesPerSecond * (targetMs / 1000.0));
            keep -= keep % _buffer.WaveFormat.BlockAlign;
            int drop = _buffer.BufferedBytes - keep;
            if (drop > 0) { var junk = new byte[drop]; _buffer.Read(junk, 0, drop); }
        }
    }

    /// <summary>0.0 .. 1.0 (may exceed 1 for boost).</summary>
    public float Volume
    {
        get => _volume?.Volume ?? 1f;
        set { if (_volume != null) _volume.Volume = Math.Max(0f, value); }
    }

    /// <summary>Output delay in milliseconds.</summary>
    public int DelayMs
    {
        get => _delay.DelayMs;
        set => _delay.DelayMs = value;
    }

    public void Dispose()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
    }

    /// <summary>
    /// Plays silence until <c>primeMs</c> of audio is buffered, then plays it. If the buffer ever runs
    /// dry it pads the gap with silence and re-primes, so one late packet costs one clean gap instead
    /// of a run of crackles while the buffer limps along near empty.
    /// </summary>
    private sealed class JitterBuffer : IWaveProvider
    {
        private readonly BufferedWaveProvider _src;
        private readonly int _primeBytes;
        private bool _priming;

        public JitterBuffer(BufferedWaveProvider src, int primeMs)
        {
            _src = src;
            _primeBytes = src.WaveFormat.AverageBytesPerSecond / 1000 * primeMs;
            _priming = _primeBytes > 0;
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_priming)
            {
                if (_src.BufferedBytes < _primeBytes + count) { Array.Clear(buffer, offset, count); return count; }
                _priming = false;
            }
            int n = _src.Read(buffer, offset, count);
            if (n < count)
            {
                Array.Clear(buffer, offset + n, count - n);
                _priming = _primeBytes > 0;
            }
            return count;
        }
    }
}
