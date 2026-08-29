namespace MeetingRecorder.Core.Audio;

internal sealed class BoundedPcmBuffer
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly int _capacityBytes;
    private byte[]? _current;
    private int _currentOffset;
    private int _bufferedBytes;

    public BoundedPcmBuffer(int capacityBytes)
    {
        if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        _capacityBytes = capacityBytes;
    }

    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return true;
        lock (_gate)
        {
            if (data.Length > _capacityBytes - _bufferedBytes) return false;
            _queue.Enqueue(data.ToArray());
            _bufferedBytes += data.Length;
            return true;
        }
    }

    public int ReadAvailable(Span<byte> destination)
    {
        var written = 0;
        lock (_gate)
        {
            while (written < destination.Length)
            {
                _current ??= _queue.Count > 0 ? _queue.Dequeue() : null;
                if (_current is null) break;
                var count = Math.Min(destination.Length - written, _current.Length - _currentOffset);
                _current.AsSpan(_currentOffset, count).CopyTo(destination[written..]);
                written += count;
                _currentOffset += count;
                _bufferedBytes -= count;
                if (_currentOffset == _current.Length)
                {
                    _current = null;
                    _currentOffset = 0;
                }
            }
        }
        return written;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _current = null;
            _currentOffset = 0;
            _bufferedBytes = 0;
        }
    }
}
