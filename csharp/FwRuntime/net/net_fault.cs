namespace Fw.Rt.Net;

public sealed record NetFaultOptions
{
    public double Loss { get; init; }
    public double Duplicate { get; init; }
    public double Reorder { get; init; }
    public int MinimumDelayMilliseconds { get; init; }
    public int MaximumDelayMilliseconds { get; init; }
    public int Seed { get; init; } = 1;
}

public sealed record NetFaultMessage<T>(long DeliverAtMilliseconds, T Payload);

public sealed class NetFaultSimulator<T>
{
    private readonly Random _random;
    private readonly NetFaultOptions _options;
    private readonly List<NetFaultMessage<T>> _pending = [];

    public NetFaultSimulator(NetFaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateProbability(options.Loss, nameof(options.Loss));
        ValidateProbability(options.Duplicate, nameof(options.Duplicate));
        ValidateProbability(options.Reorder, nameof(options.Reorder));
        if (options.MinimumDelayMilliseconds < 0
            || options.MaximumDelayMilliseconds < options.MinimumDelayMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        _options = options;
        _random = new Random(options.Seed);
    }

    public int PendingCount => _pending.Count;

    public void Enqueue(T payload, long nowMilliseconds)
    {
        if (_random.NextDouble() < _options.Loss)
        {
            return;
        }
        Add(payload, nowMilliseconds);
        if (_random.NextDouble() < _options.Duplicate)
        {
            Add(payload, nowMilliseconds);
        }
        if (_pending.Count > 1 && _random.NextDouble() < _options.Reorder)
        {
            int last = _pending.Count - 1;
            (_pending[last - 1], _pending[last]) = (_pending[last], _pending[last - 1]);
        }
    }

    public IReadOnlyList<T> Receive(long nowMilliseconds, int maxMessages = int.MaxValue)
    {
        if (maxMessages <= 0 || _pending.Count == 0)
        {
            return [];
        }
        var output = new List<T>();
        for (int index = 0; index < _pending.Count && output.Count < maxMessages;)
        {
            NetFaultMessage<T> item = _pending[index];
            if (item.DeliverAtMilliseconds > nowMilliseconds)
            {
                index += 1;
                continue;
            }
            output.Add(item.Payload);
            _pending.RemoveAt(index);
        }
        return output;
    }

    private void Add(T payload, long nowMilliseconds)
    {
        int delay = _random.Next(
            _options.MinimumDelayMilliseconds,
            _options.MaximumDelayMilliseconds + 1
        );
        _pending.Add(new NetFaultMessage<T>(nowMilliseconds + delay, payload));
    }

    private static void ValidateProbability(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
