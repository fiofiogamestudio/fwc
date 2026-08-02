using System.Collections.ObjectModel;

namespace Fw.Rt.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    IReadOnlyDictionary<string, object?> Data
);

public interface ILogSink
{
    void Write(LogEntry entry);
}

public sealed class LogBuffer : ILogSink
{
    private static readonly object ForwardGraphGate = new();

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = [];
    private readonly Dictionary<string, LogLevel> _categoryLevels = new(StringComparer.Ordinal);
    private long _sequence;
    private LogLevel _minimumLevel = LogLevel.Info;
    private int _capacity = 200;
    private ILogSink? _forwardTo;

    public LogLevel MinimumLevel
    {
        get
        {
            lock (_gate)
            {
                return _minimumLevel;
            }
        }
        set
        {
            lock (_gate)
            {
                _minimumLevel = value;
            }
        }
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
            lock (_gate)
            {
                _capacity = Math.Max(0, value);
                TrimLocked();
            }
        }
    }

    public ILogSink? ForwardTo
    {
        get
        {
            lock (_gate)
            {
                return _forwardTo;
            }
        }
        set
        {
            lock (ForwardGraphGate)
            {
                if (value is LogBuffer buffer && buffer.ReachesTargetOrCycle(this))
                {
                    throw new ArgumentException(
                        "LogBuffer forwarding cannot contain a cycle.",
                        nameof(value)
                    );
                }
                lock (_gate)
                {
                    _forwardTo = value;
                }
            }
        }
    }

    public event Action<LogEntry>? Written;

    public void SetCategoryLevel(string category, LogLevel level)
    {
        lock (_gate)
        {
            _categoryLevels[category ?? string.Empty] = level;
        }
    }

    public bool ClearCategoryLevel(string category)
    {
        lock (_gate)
        {
            return _categoryLevels.Remove(category ?? string.Empty);
        }
    }

    public bool IsEnabled(LogLevel level, string category = "")
    {
        lock (_gate)
        {
            var threshold = _categoryLevels.TryGetValue(category ?? string.Empty, out var categoryLevel)
                ? categoryLevel
                : _minimumLevel;
            return level >= threshold;
        }
    }

    public LogEntry? Add(
        LogLevel level,
        string category,
        string message,
        IReadOnlyDictionary<string, object?>? data = null
    )
    {
        if (!IsEnabled(level, category))
        {
            return null;
        }
        var entry = new LogEntry(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            level,
            category ?? string.Empty,
            message ?? string.Empty,
            data ?? new Dictionary<string, object?>()
        );
        Write(entry);
        return entry;
    }

    public void Write(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var stored = entry with { Data = SnapshotData(entry.Data) };
        ILogSink? forwardTo;
        Action<LogEntry>? written;
        lock (_gate)
        {
            _entries.Enqueue(stored);
            TrimLocked();
            forwardTo = _forwardTo;
            written = Written;
        }
        forwardTo?.Write(stored);
        written?.Invoke(stored);
    }

    public IReadOnlyList<LogEntry> Recent(int limit = -1)
    {
        lock (_gate)
        {
            var values = _entries.ToArray();
            if (limit < 0 || limit >= values.Length)
            {
                return values;
            }
            return values[^limit..];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void TrimLocked()
    {
        while (_entries.Count > _capacity)
        {
            _entries.Dequeue();
        }
    }

    private static IReadOnlyDictionary<string, object?> SnapshotData(
        IReadOnlyDictionary<string, object?> data
    )
    {
        return new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(data, StringComparer.Ordinal)
        );
    }

    private bool ReachesTargetOrCycle(LogBuffer target)
    {
        ILogSink? current = this;
        var visited = new HashSet<LogBuffer>(ReferenceEqualityComparer.Instance);
        while (current is LogBuffer buffer)
        {
            if (!visited.Add(buffer) || ReferenceEquals(buffer, target))
            {
                return true;
            }
            lock (buffer._gate)
            {
                current = buffer._forwardTo;
            }
        }
        return false;
    }
}
