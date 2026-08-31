using System.Collections.ObjectModel;

namespace Fw.Rt.AI.Core;

public sealed class Blackboard
{
    private const int MaxKeyLength = 128;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public int Count => _values.Count;
    public IEnumerable<string> Keys => _values.Keys;

    public bool Contains(string key)
    {
        return _values.ContainsKey(NormalizeKey(key));
    }

    public void Set(string key, object? value)
    {
        _values[NormalizeKey(key)] = value;
    }

    public bool Remove(string key)
    {
        return _values.Remove(NormalizeKey(key));
    }

    public bool TryGet<T>(string key, out T value)
    {
        if (_values.TryGetValue(NormalizeKey(key), out object? raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }

    public object? Value(string key, object? fallback = null)
    {
        return _values.TryGetValue(NormalizeKey(key), out object? value) ? value : fallback;
    }

    public double Number(string key, double fallback = 0.0)
    {
        object? value = Value(key);
        double result = value switch
        {
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => number,
            decimal number => (double)number,
            _ => fallback,
        };
        return double.IsFinite(result) ? result : fallback;
    }

    public bool Boolean(string key, bool fallback = false)
    {
        return Value(key) is bool value ? value : fallback;
    }

    public string Text(string key, string fallback = "")
    {
        return Value(key) is string value ? value : fallback;
    }

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        return new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(_values, StringComparer.Ordinal)
        );
    }

    public Blackboard Clone()
    {
        var clone = new Blackboard();
        clone.CopyFrom(this);
        return clone;
    }

    public void CopyFrom(Blackboard other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _values.Clear();
        foreach ((string key, object? value) in other._values)
        {
            _values[key] = value;
        }
    }

    public void Clear()
    {
        _values.Clear();
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string value = key.Trim();
        if (value.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                $"Decision blackboard key exceeds {MaxKeyLength} characters.",
                nameof(key)
            );
        }
        return value;
    }
}
