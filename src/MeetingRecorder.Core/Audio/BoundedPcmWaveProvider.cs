using NAudio.Wave;

namespace MeetingRecorder.Core.Audio;

public sealed class BoundedPcmWaveProvider : IWaveProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly int _maxBufferedBytes;
    private byte[]? _current;
    private int _currentOffset;
    private int _bufferedBytes;
    private bool _completed;
    private bool _disposed;

    public BoundedPcmWaveProvider(int sampleRate = 48_000, int bitsPerSample = 16, int channels = 2, TimeSpan? capacity = null)
    {
        WaveFormat = new WaveFormat(sampleRate, bitsPerSample, channels);
        var duration = capacity ?? TimeSpan.FromSeconds(10);
        _maxBufferedBytes = checked((int)Math.Ceiling(WaveFormat.AverageBytesPerSecond * duration.TotalSeconds));
    }

    public WaveFormat WaveFormat { get; }
    public int MaxBufferedBytes => _maxBufferedBytes;
    public int BufferedBytes { get { lock (_gate) return _bufferedBytes; } }

    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return true;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) return false;
            if (data.Length > _maxBufferedBytes - _bufferedBytes) return false;
            _queue.Enqueue(data.ToArray());
            _bufferedBytes += data.Length;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    public int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            while (!_disposed && !_completed && _current is null && _queue.Count == 0)
            {
                Monitor.Wait(_gate);
            }

            if (_disposed) return 0;
            _current ??= _queue.Count > 0 ? _queue.Dequeue() : null;
            if (_current is null) return 0;

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsSpan(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            _bufferedBytes -= count;
            if (_currentOffset == _current.Length)
            {
                _current = null;
                _currentOffset = 0;
            }
            Monitor.PulseAll(_gate);
            return count;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _queue.Clear();
            _current = null;
            _bufferedBytes = 0;
            Monitor.PulseAll(_gate);
        }
    }
}
