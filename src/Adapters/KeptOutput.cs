using System.Text;

namespace Snail.MCP.Sandbox.Adapters;

/// <summary>One output stream of a run, kept within a byte limit: its beginning and its end, with the cut named between them.</summary>
/// <remarks>
/// Half the limit goes to each end. A traceback is read from the bottom and a progress log from the top, and
/// keeping only the head, as a plain cut would, drops the one line that says what failed. Appends come from
/// the reading task and the text may be taken by the timeout path while it still runs, hence the lock.
/// </remarks>
internal sealed class KeptOutput(int limit)
{
    private readonly Lock _gate = new();

    private readonly byte[] _head = new byte[limit / 2];

    private readonly byte[] _tail = new byte[limit - limit / 2];

    private int _headCount;

    private int _tailEnd;

    private int _tailCount;

    private long _total;

    public bool IsCut
    {
        get
        {
            lock (_gate)
                return _total > limit;
        }
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                var tailStart = _tailCount < _tail.Length ? 0 : _tailEnd;
                byte[] tail = [.. _tail.AsSpan(tailStart, _tailCount - tailStart), .. _tail.AsSpan(0, tailStart)];

                if (_total <= limit)
                    return Encoding.UTF8.GetString([.. _head.AsSpan(0, _headCount), .. tail]);

                return $"{Encoding.UTF8.GetString(_head)}\n[... {_total - limit} bytes cut ...]\n{Encoding.UTF8.GetString(tail)}";
            }
        }
    }

    public void Append(ReadOnlySpan<byte> chunk)
    {
        lock (_gate)
        {
            _total += chunk.Length;

            var intoHead = Math.Min(_head.Length - _headCount, chunk.Length);
            chunk[..intoHead].CopyTo(_head.AsSpan(_headCount));
            _headCount += intoHead;

            KeepTail(chunk[intoHead..]);
        }
    }

    private void KeepTail(ReadOnlySpan<byte> rest)
    {
        var kept = rest.Length > _tail.Length ? rest[^_tail.Length..] : rest;
        var untilWrap = Math.Min(_tail.Length - _tailEnd, kept.Length);

        kept[..untilWrap].CopyTo(_tail.AsSpan(_tailEnd));
        kept[untilWrap..].CopyTo(_tail);

        _tailEnd = (_tailEnd + kept.Length) % _tail.Length;
        _tailCount = Math.Min(_tail.Length, _tailCount + kept.Length);
    }
}
