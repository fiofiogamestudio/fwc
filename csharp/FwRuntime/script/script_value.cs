using System.Collections.ObjectModel;

namespace Fw.Rt.Script;

public sealed record ScriptCommand(string Name, IReadOnlyList<object?> Args);

public sealed record ScriptCallResult(
    object? Value,
    IReadOnlyList<ScriptCommand> Commands,
    int Instructions
);

public sealed record ScriptRuntimeOptions(
    int LoadInstructionLimit = 100_000,
    int CallInstructionLimit = 20_000,
    int MaxValueDepth = 32,
    int MaxCollectionItems = 16_384,
    int MaxStringLength = 1_048_576,
    int MaxSourceLength = 1_048_576,
    int MaxCommands = 1_024
)
{
    public ScriptRuntimeOptions Validate()
    {
        if (LoadInstructionLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LoadInstructionLimit));
        }
        if (CallInstructionLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CallInstructionLimit));
        }
        if (MaxValueDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxValueDepth));
        }
        if (MaxCollectionItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCollectionItems));
        }
        if (MaxStringLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxStringLength));
        }
        if (MaxSourceLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSourceLength));
        }
        if (MaxCommands <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCommands));
        }
        return this;
    }
}

public sealed class ScriptHost
{
    private readonly Dictionary<string, Func<IReadOnlyList<object?>, object?>> _queries =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Queries =>
        new ReadOnlyCollection<string>(_queries.Keys.Order(StringComparer.Ordinal).ToArray());

    public void RegisterQuery(string name, Func<IReadOnlyList<object?>, object?> query)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Script query name cannot be empty.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(query);
        if (!_queries.TryAdd(name.Trim(), query))
        {
            throw new InvalidOperationException($"Duplicate script query: {name}");
        }
    }

    internal object? Query(string name, IReadOnlyList<object?> args)
    {
        if (!_queries.TryGetValue(name, out var query))
        {
            throw new InvalidOperationException($"Script requested unknown query: {name}");
        }
        return query(args);
    }
}

public class ScriptRuntimeException : Exception
{
    public ScriptRuntimeException(string message)
        : base(message)
    {
    }

    public ScriptRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ScriptBudgetException : ScriptRuntimeException
{
    public ScriptBudgetException(string module, int limit)
        : base($"Script '{module}' exceeded its {limit} instruction budget.")
    {
        Module = module;
        Limit = limit;
    }

    public string Module { get; }
    public int Limit { get; }
}
