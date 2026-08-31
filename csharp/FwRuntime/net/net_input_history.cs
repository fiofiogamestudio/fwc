namespace Fw.Rt.Net;

public sealed record NetInputFrame<TState>(uint Tick, TState State);

public sealed class NetInputHistory<TState>
{
    private readonly NetInputFrame<TState>?[] _frames;
    private int _next;
    private int _count;

    public NetInputHistory(int capacity = 6)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _frames = new NetInputFrame<TState>[capacity];
    }

    public int Count => _count;
    public int Capacity => _frames.Length;

    public void Capture(uint tick, TState state)
    {
        if (_count > 0)
        {
            NetInputFrame<TState> latest = GetNewest(0);
            if (!IsNewer(tick, latest.Tick))
            {
                if (tick == latest.Tick)
                {
                    _frames[PreviousIndex(_next)] = new NetInputFrame<TState>(tick, state);
                }
                return;
            }
        }

        _frames[_next] = new NetInputFrame<TState>(tick, state);
        _next = (_next + 1) % _frames.Length;
        _count = Math.Min(_count + 1, _frames.Length);
    }

    public IReadOnlyList<NetInputFrame<TState>> NewestFirst(int maxFrames)
    {
        int take = Math.Min(Math.Max(maxFrames, 0), _count);
        if (take == 0)
        {
            return [];
        }
        var output = new NetInputFrame<TState>[take];
        for (int index = 0; index < take; index++)
        {
            output[index] = GetNewest(index);
        }
        return output;
    }

    public bool TryLatest(out NetInputFrame<TState>? frame)
    {
        frame = _count == 0 ? null : GetNewest(0);
        return frame != null;
    }

    public void Clear()
    {
        Array.Clear(_frames);
        _next = 0;
        _count = 0;
    }

    private NetInputFrame<TState> GetNewest(int offset)
    {
        int index = (_next - 1 - offset + _frames.Length) % _frames.Length;
        return _frames[index] ?? throw new InvalidOperationException("Input history is sparse.");
    }

    private int PreviousIndex(int index)
    {
        return (index - 1 + _frames.Length) % _frames.Length;
    }

    private static bool IsNewer(uint candidate, uint current)
    {
        return candidate != current && unchecked(candidate - current) < 0x80000000u;
    }
}
