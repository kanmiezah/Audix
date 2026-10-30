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
    private readonly AdjustableDelayProvider _delay;
    private VolumeSampleProvider? _volume;
    private WasapiOut? _out;

    public string DeviceId => _deviceId;
    public string DeviceName { get; }

    public OutputSink(string deviceId, string deviceName)
    {
        _deviceId = deviceId;
        DeviceName = deviceName;
        _buffer = new BufferedWaveProvider(ProcessCapture.CaptureFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500), // hard cap on how far we can lag
            DiscardOnBufferOverflow = true,
        };
        _delay = new AdjustableDelayProvider(_buffer);
    }

    // If the queue backs up past MaxLagMs (clock drift, a burst, a device stall), the
    // backlog would become permanent lag — so we drop it back down to ResyncMs ("live").
    // Keep the window TIGHT so two outputs of the same app can't drift far apart on their
    // own (device-inherent latency differences are handled by the per-output delay control).
    private const int MaxLagMs = 140;
    private const int ResyncMs = 110;

    public void Start()
    {
        var device = new MMDeviceEnumerator().GetDevice(_deviceId);
        var mix = device.AudioClient.MixFormat;

        // Chain: 16-bit capture -> delay -> (sample) volume -> float -> resample to device mix.
        // Volume is applied in the PCM/sample domain (ToSampleProvider only supports PCM/float,
        // not the device's WaveFormatExtensible mix), then we resample the plain float output
        // to whatever the device actually wants.
        _volume = new VolumeSampleProvider(_delay.ToSampleProvider()) { Volume = 1.0f };
        IWaveProvider floatStream = _volume.ToWaveProvider();          // 32-bit IEEE float, 48 kHz
        IWaveProvider outStream = new MediaFoundationResampler(floatStream, mix) { ResamplerQuality = 60 };

        // Event-driven, low output-buffer latency (was 80 ms). ~35 ms is safe in shared mode.
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, 35);
        _out.Init(outStream);
        _out.Play();
    }

    /// <summary>Feed captured PCM bytes (in ProcessCapture.CaptureFormat) to this sink.</summary>
    public void Feed(byte[] data, int bytes)
    {
        _buffer.AddSamples(data, 0, bytes);
        // Drop any backlog beyond MaxLagMs so latency can't creep up over time.
        if (_buffer.BufferedDuration.TotalMilliseconds > MaxLagMs)
        {
            int keep = (int)(_buffer.WaveFormat.AverageBytesPerSecond * (ResyncMs / 1000.0));
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
}
