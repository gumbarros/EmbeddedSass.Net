using System.Text;

namespace EmbeddedSass.Net.Internal.Process;

internal sealed class BoundedByteTail
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _start;
    private int _count;

    public BoundedByteTail(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _buffer = new byte[capacity];
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_buffer.Length == 0 || bytes.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (bytes.Length >= _buffer.Length)
            {
                bytes[^_buffer.Length..].CopyTo(_buffer);
                _start = 0;
                _count = _buffer.Length;
                return;
            }

            foreach (byte value in bytes)
            {
                int index = (_start + _count) % _buffer.Length;
                _buffer[index] = value;
                if (_count == _buffer.Length)
                {
                    _start = (_start + 1) % _buffer.Length;
                }
                else
                {
                    _count++;
                }
            }
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return string.Empty;
            }

            byte[] ordered = new byte[_count];
            int firstLength = Math.Min(_count, _buffer.Length - _start);
            _buffer.AsSpan(_start, firstLength).CopyTo(ordered);
            if (firstLength < _count)
            {
                _buffer.AsSpan(0, _count - firstLength).CopyTo(ordered.AsSpan(firstLength));
            }

            return Encoding.UTF8.GetString(ordered);
        }
    }
}
