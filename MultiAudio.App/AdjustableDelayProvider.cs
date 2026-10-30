using NAudio.Wave;

namespace MultiAudio.App;

/// <summary>
/// Wraps a source provider and imposes a live-adjustable output delay (in ms) by
/// injecting silence to grow the delay or dropping buffered audio to shrink it.
/// Always fills the full requested buffer so the WASAPI output never underruns.
/// This is the per-output synchronization control (spec §6).
/// </summary>
public sealed class AdjustableDelayProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private readonly int _bytesPerMs;
    private readonly int _blockAlign;

    private volatile int _targetDelayBytes;
    private int _currentDelayBytes;

    public AdjustableDelayProvider(IWaveProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _blockAlign = WaveFormat.BlockAlign;
        _bytesPerMs = WaveFormat.AverageBytesPerSecond / 1000;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Desired output delay in milliseconds (0..).</summary>
    public int DelayMs
    {
        get => _targetDelayBytes / Math.Max(1, _bytesPerMs);
        set => _targetDelayBytes = Align(Math.Max(0, value) * _bytesPerMs);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int target = _targetDelayBytes;

        // Shrink delay: drop already-buffered source audio.
        if (_currentDelayBytes > target)
        {
            int drop = Align(_currentDelayBytes - target);
            var scratch = new byte[drop];
            int dropped = _source.Read(scratch, 0, drop);
            _currentDelayBytes -= dropped;
        }

        int written = 0;
        while (written < count)
        {
            if (_currentDelayBytes < target)
            {
                // Grow delay by emitting silence.
                int sil = Math.Min(count - written, target - _currentDelayBytes);
                Array.Clear(buffer, offset + written, sil);
                written += sil;
                _currentDelayBytes += sil;
            }
            else
            {
                int n = _source.Read(buffer, offset + written, count - written);
                written += n;
                if (n == 0) break; // source exhausted (shouldn't happen with a live buffer)
            }
        }
        return written;
    }

    private int Align(int bytes) => bytes - (bytes % _blockAlign);
}
