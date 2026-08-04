using System.Collections.ObjectModel;

namespace Fw.Rt.Localization;

public sealed class LocalizedMessage
{
    public LocalizedMessage(
        string id,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string fallback = ""
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Arguments = arguments == null
            ? ReadOnlyDictionary<string, object?>.Empty
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
            );
        Fallback = fallback ?? string.Empty;
    }

    public string Id { get; }

    public IReadOnlyDictionary<string, object?> Arguments { get; }

    public string Fallback { get; }

    public LocalizedMessage WithArgument(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var arguments = new Dictionary<string, object?>(Arguments, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return new LocalizedMessage(Id, arguments, Fallback);
    }

    public IReadOnlyDictionary<string, object?> ToPayload()
    {
        return new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Id,
                ["args"] = Arguments,
                ["fallback"] = Fallback,
            }
        );
    }
}

public sealed class LocalizedAsset
{
    public LocalizedAsset(string id, string fallback = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Fallback = fallback ?? string.Empty;
    }

    public string Id { get; }

    public string Fallback { get; }
}
