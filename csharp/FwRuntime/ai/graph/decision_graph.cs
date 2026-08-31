using System.Collections.ObjectModel;
using System.Text.Json;

namespace Fw.Rt.AI.Graph;

public class DecisionGraphException : Exception
{
    public DecisionGraphException(string message)
        : base(message)
    {
    }

    public DecisionGraphException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DecisionGraphBudgetException : DecisionGraphException
{
    public DecisionGraphBudgetException()
        : base("Decision graph exhausted its execution budget.")
    {
    }
}

public sealed record DecisionGraphNode(
    int Id,
    string Type,
    IReadOnlyDictionary<string, object?> Values
);

public sealed record DecisionGraphEdge(
    string Id,
    int FromNode,
    string FromPort,
    int ToNode,
    string ToPort
);

public sealed class DecisionGraph
{
    private const int MaxNodes = 16_384;
    private const int MaxEdges = 32_768;
    private const int MaxJsonLength = 4_194_304;
    private const int MaxIdentifierLength = 128;
    private readonly IReadOnlyDictionary<int, DecisionGraphNode> _nodesById;

    private DecisionGraph(
        string id,
        IReadOnlyList<DecisionGraphNode> nodes,
        IReadOnlyList<DecisionGraphEdge> edges
    )
    {
        Id = id;
        Nodes = nodes;
        Edges = edges;
        _nodesById = new ReadOnlyDictionary<int, DecisionGraphNode>(
            nodes.ToDictionary(node => node.Id)
        );
    }

    public string Id { get; }
    public IReadOnlyList<DecisionGraphNode> Nodes { get; }
    public IReadOnlyList<DecisionGraphEdge> Edges { get; }

    public DecisionGraphNode Node(int id)
    {
        return _nodesById.TryGetValue(id, out DecisionGraphNode? node)
            ? node
            : throw new DecisionGraphException($"Decision graph '{Id}' has no node {id}.");
    }

    public IReadOnlyList<DecisionGraphNode> NodesOfType(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return Nodes.Where(node => string.Equals(node.Type, type, StringComparison.Ordinal)).ToArray();
    }

    public IReadOnlyList<DecisionGraphNode> Children(int nodeId, string port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        return Edges
            .Where(edge => edge.FromNode == nodeId
                && string.Equals(edge.FromPort, port, StringComparison.Ordinal))
            .Select(edge => Node(edge.ToNode))
            .OrderBy(NodeOrder)
            .ThenBy(node => node.Id)
            .ToArray();
    }

    public DecisionGraphNode? Input(int nodeId, string port, bool required = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        DecisionGraphEdge[] edges = Edges
            .Where(edge => edge.ToNode == nodeId
                && string.Equals(edge.ToPort, port, StringComparison.Ordinal))
            .ToArray();
        if (edges.Length > 1)
        {
            throw new DecisionGraphException(
                $"Decision graph '{Id}' node {nodeId} has multiple inputs on '{port}'."
            );
        }
        if (edges.Length == 0)
        {
            if (required)
            {
                throw new DecisionGraphException(
                    $"Decision graph '{Id}' node {nodeId} requires input '{port}'."
                );
            }
            return null;
        }
        return Node(edges[0].FromNode);
    }

    public static DecisionGraph Compose(
        string id,
        IEnumerable<DecisionGraph> fragments
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(fragments);
        if (id.Length > MaxIdentifierLength)
        {
            throw new DecisionGraphException("Composed decision graph id is too long.");
        }

        DecisionGraph[] sources = fragments.ToArray();
        if (sources.Length == 0)
        {
            throw new DecisionGraphException("Decision graph composition needs fragments.");
        }
        if (sources.Any(source => source == null))
        {
            throw new DecisionGraphException("Decision graph composition contains a null fragment.");
        }
        string? duplicateId = sources
            .GroupBy(source => source.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateId != null)
        {
            throw new DecisionGraphException(
                $"Decision graph composition has duplicate fragment id '{duplicateId}'."
            );
        }

        var nodes = new List<DecisionGraphNode>();
        var edges = new List<DecisionGraphEdge>();
        var roots = new Dictionary<string, DecisionGraphNode>(StringComparer.Ordinal);
        int nextNodeId = 1;
        int nextEdgeId = 1;
        foreach (DecisionGraph source in sources)
        {
            var idMap = new Dictionary<int, int>();
            foreach (DecisionGraphNode node in source.Nodes)
            {
                string rootKey = RootKey(node);
                if (rootKey.Length > 0 && roots.TryGetValue(rootKey, out DecisionGraphNode? root))
                {
                    if (!ValuesEqual(root.Values, node.Values))
                    {
                        throw new DecisionGraphException(
                            $"Decision graph fragments disagree on root '{rootKey}'."
                        );
                    }
                    idMap[node.Id] = root.Id;
                    continue;
                }

                var copy = new DecisionGraphNode(nextNodeId, node.Type, node.Values);
                nodes.Add(copy);
                idMap[node.Id] = nextNodeId;
                nextNodeId += 1;
                if (rootKey.Length > 0)
                {
                    roots.Add(rootKey, copy);
                }
            }
            foreach (DecisionGraphEdge edge in source.Edges)
            {
                edges.Add(new DecisionGraphEdge(
                    $"e_{nextEdgeId}",
                    idMap[edge.FromNode],
                    edge.FromPort,
                    idMap[edge.ToNode],
                    edge.ToPort
                ));
                nextEdgeId += 1;
            }
        }
        if (nodes.Count > MaxNodes)
        {
            throw new DecisionGraphException(
                $"Composed decision graph exceeds the {MaxNodes} node limit."
            );
        }
        if (edges.Count > MaxEdges)
        {
            throw new DecisionGraphException(
                $"Composed decision graph exceeds the {MaxEdges} edge limit."
            );
        }
        return new DecisionGraph(id, nodes.AsReadOnly(), edges.AsReadOnly());
    }

    public static DecisionGraph Parse(string json, string source = "decision.json")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DecisionGraphException($"Decision graph is empty: {source}");
        }
        if (json.Length > MaxJsonLength)
        {
            throw new DecisionGraphException(
                $"Decision graph exceeds the {MaxJsonLength} character limit: {source}"
            );
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DecisionGraphException($"Decision graph root must be an object: {source}");
            }

            string id = RequiredString(root, "id", source);
            JsonElement nodesElement = RequiredArray(root, "nodes", source);
            JsonElement edgesElement = RequiredArray(root, "edges", source);
            if (nodesElement.GetArrayLength() > MaxNodes)
            {
                throw new DecisionGraphException(
                    $"Decision graph exceeds the {MaxNodes} node limit: {source}"
                );
            }
            if (edgesElement.GetArrayLength() > MaxEdges)
            {
                throw new DecisionGraphException(
                    $"Decision graph exceeds the {MaxEdges} edge limit: {source}"
                );
            }

            var nodes = new List<DecisionGraphNode>();
            var nodeIds = new HashSet<int>();
            foreach (JsonElement element in nodesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new DecisionGraphException($"Decision graph node must be an object: {source}");
                }
                int nodeId = RequiredInt(element, "id", source);
                if (!nodeIds.Add(nodeId))
                {
                    throw new DecisionGraphException($"Duplicate decision graph node id {nodeId}: {source}");
                }
                string type = RequiredString(element, "type", source);
                IReadOnlyDictionary<string, object?> values = element.TryGetProperty(
                    "values",
                    out JsonElement valuesElement
                ) ? ReadObject(valuesElement, source) : ReadOnlyDictionary<string, object?>.Empty;
                nodes.Add(new DecisionGraphNode(nodeId, type, values));
            }
            if (nodes.Count == 0)
            {
                throw new DecisionGraphException($"Decision graph needs at least one node: {source}");
            }

            var edges = new List<DecisionGraphEdge>();
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement element in edgesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new DecisionGraphException($"Decision graph edge must be an object: {source}");
                }
                string edgeId = RequiredString(element, "id", source);
                if (!edgeIds.Add(edgeId))
                {
                    throw new DecisionGraphException($"Duplicate decision graph edge id '{edgeId}': {source}");
                }
                JsonElement from = RequiredObject(element, "from", source);
                JsonElement to = RequiredObject(element, "to", source);
                int fromNode = RequiredInt(from, "node", source);
                int toNode = RequiredInt(to, "node", source);
                if (!nodeIds.Contains(fromNode) || !nodeIds.Contains(toNode))
                {
                    throw new DecisionGraphException(
                        $"Decision graph edge '{edgeId}' references a missing node: {source}"
                    );
                }
                edges.Add(new DecisionGraphEdge(
                    edgeId,
                    fromNode,
                    RequiredString(from, "port", source),
                    toNode,
                    RequiredString(to, "port", source)
                ));
            }

            return new DecisionGraph(id, nodes.AsReadOnly(), edges.AsReadOnly());
        }
        catch (JsonException exception)
        {
            throw new DecisionGraphException(
                $"Invalid decision graph JSON '{source}': {exception.Message}",
                exception
            );
        }
    }

    internal static string Text(
        IReadOnlyDictionary<string, object?> values,
        string name,
        string fallback = ""
    )
    {
        return values.TryGetValue(name, out object? raw) && raw is string value
            ? value.Trim()
            : fallback;
    }

    internal static bool Boolean(
        IReadOnlyDictionary<string, object?> values,
        string name,
        bool fallback = false
    )
    {
        return values.TryGetValue(name, out object? raw) && raw is bool value ? value : fallback;
    }

    internal static double Number(
        IReadOnlyDictionary<string, object?> values,
        string name,
        double fallback = 0.0
    )
    {
        if (!values.TryGetValue(name, out object? raw))
        {
            return fallback;
        }
        double value = raw switch
        {
            long integer => integer,
            double number => number,
            _ => fallback,
        };
        return double.IsFinite(value) ? value : fallback;
    }

    internal static IReadOnlyList<string> TextList(
        IReadOnlyDictionary<string, object?> values,
        string name
    )
    {
        if (!values.TryGetValue(name, out object? raw)
            || raw is not IReadOnlyList<object?> list)
        {
            return [];
        }
        return list
            .OfType<string>()
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int NodeOrder(DecisionGraphNode node)
    {
        double value = Number(node.Values, "order", node.Id);
        return value <= int.MinValue ? int.MinValue
            : value >= int.MaxValue ? int.MaxValue
            : (int)value;
    }

    private static string RootKey(DecisionGraphNode node)
    {
        if (node.Type is not ("UtilityRoot" or "StateTreeRoot" or "GoapRoot"))
        {
            return "";
        }
        string name = Text(node.Values, "name");
        return name.Length == 0 ? "" : $"{node.Type}:{name}";
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is IReadOnlyDictionary<string, object?> leftMap
            && right is IReadOnlyDictionary<string, object?> rightMap)
        {
            return leftMap.Count == rightMap.Count
                && leftMap.All(pair => rightMap.TryGetValue(pair.Key, out object? value)
                    && ValuesEqual(pair.Value, value));
        }
        if (left is IReadOnlyList<object?> leftList
            && right is IReadOnlyList<object?> rightList)
        {
            return leftList.Count == rightList.Count
                && leftList.Zip(rightList).All(pair => ValuesEqual(pair.First, pair.Second));
        }
        return Equals(left, right);
    }

    private static JsonElement RequiredArray(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new DecisionGraphException($"Decision graph '{source}' requires array '{name}'.");
        }
        return value;
    }

    private static JsonElement RequiredObject(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new DecisionGraphException($"Decision graph '{source}' requires object '{name}'.");
        }
        return value;
    }

    private static string RequiredString(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new DecisionGraphException($"Decision graph '{source}' requires string '{name}'.");
        }
        string result = value.GetString()?.Trim() ?? "";
        if (result.Length == 0 || result.Length > MaxIdentifierLength)
        {
            throw new DecisionGraphException(
                $"Decision graph '{source}' has invalid '{name}' length."
            );
        }
        return result;
    }

    private static int RequiredInt(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int result))
        {
            throw new DecisionGraphException($"Decision graph '{source}' requires integer '{name}'.");
        }
        return result;
    }

    private static IReadOnlyDictionary<string, object?> ReadObject(JsonElement value, string source)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DecisionGraphException($"Expected JSON object in decision graph: {source}");
        }
        var result = value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ReadValue(property.Value, source),
            StringComparer.Ordinal
        );
        return new ReadOnlyDictionary<string, object?>(result);
    }

    private static object? ReadValue(JsonElement value, string source)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => Array.AsReadOnly(
                value.EnumerateArray().Select(item => ReadValue(item, source)).ToArray()
            ),
            JsonValueKind.Object => ReadObject(value, source),
            _ => throw new DecisionGraphException(
                $"Unsupported JSON value in decision graph: {source}"
            ),
        };
    }
}
