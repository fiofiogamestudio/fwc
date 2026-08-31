using System.Collections.ObjectModel;

namespace Fw.Rt.AI.Core;

public sealed record DecisionTraceEntry(
    int Tick,
    string Module,
    string Event,
    IReadOnlyDictionary<string, object?> Data
);

public interface IDecisionTrace
{
    void Write(
        int tick,
        string module,
        string @event,
        IReadOnlyDictionary<string, object?>? data = null
    );
}

public sealed class DecisionTrace : IDecisionTrace
{
    private readonly object _gate = new();
    private readonly Queue<DecisionTraceEntry> _entries = [];
    private int _capacity;

    public DecisionTrace(int capacity = 256)
    {
        Capacity = capacity;
    }

    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _capacity;
            }
        }
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            lock (_gate)
            {
                _capacity = value;
                Trim();
            }
        }
    }

    public void Write(
        int tick,
        string module,
        string @event,
        IReadOnlyDictionary<string, object?>? data = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(@event);
        var snapshot = data == null
            ? ReadOnlyDictionary<string, object?>.Empty
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(data, StringComparer.Ordinal)
            );
        lock (_gate)
        {
            if (_capacity == 0)
            {
                return;
            }
            _entries.Enqueue(new DecisionTraceEntry(tick, module, @event, snapshot));
            Trim();
        }
    }

    public IReadOnlyList<DecisionTraceEntry> Recent(int count = int.MaxValue)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        lock (_gate)
        {
            return _entries.Skip(Math.Max(0, _entries.Count - count)).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            _entries.Dequeue();
        }
    }
}
