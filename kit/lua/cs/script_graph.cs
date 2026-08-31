using System.Collections.ObjectModel;
using System.Text.Json;

namespace Fw.Rt.Script;

public sealed record ScriptGraphNode(
    int Id,
    string Type,
    IReadOnlyDictionary<string, object?> Values
);

public sealed record ScriptGraphEdge(
    string Id,
    int FromNode,
    string FromPort,
    int ToNode,
    string ToPort
);

public sealed class ScriptGraph
{
    private const int MaxNodes = 16_384;
    private const int MaxEdges = 32_768;
    private const int MaxJsonLength = 4_194_304;
    private const int MaxIdentifierLength = 128;

    private ScriptGraph(
        string id,
        IReadOnlyList<ScriptGraphNode> nodes,
        IReadOnlyList<ScriptGraphEdge> edges,
        IReadOnlyDictionary<string, object?> data
    )
    {
        Id = id;
        Nodes = nodes;
        Edges = edges;
        Data = data;
    }

    public string Id { get; }
    public IReadOnlyList<ScriptGraphNode> Nodes { get; }
    public IReadOnlyList<ScriptGraphEdge> Edges { get; }
    public IReadOnlyDictionary<string, object?> Data { get; }

    public static ScriptGraph Parse(string json, string source = "graph.json")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ScriptRuntimeException($"Script graph is empty: {source}");
        }
        if (json.Length > MaxJsonLength)
        {
            throw new ScriptRuntimeException(
                $"Script graph exceeds the {MaxJsonLength} character limit: {source}"
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
                throw new ScriptRuntimeException($"Script graph root must be an object: {source}");
            }

            string id = RequiredString(root, "id", source);
            JsonElement nodesElement = RequiredArray(root, "nodes", source);
            JsonElement edgesElement = RequiredArray(root, "edges", source);
            if (nodesElement.GetArrayLength() > MaxNodes)
            {
                throw new ScriptRuntimeException(
                    $"Script graph exceeds the {MaxNodes} node limit: {source}"
                );
            }
            if (edgesElement.GetArrayLength() > MaxEdges)
            {
                throw new ScriptRuntimeException(
                    $"Script graph exceeds the {MaxEdges} edge limit: {source}"
                );
            }
            var nodes = new List<ScriptGraphNode>();
            var nodeIds = new HashSet<int>();
            foreach (JsonElement node in nodesElement.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    throw new ScriptRuntimeException($"Script graph node must be an object: {source}");
                }
                int nodeId = RequiredInt(node, "id", source);
                if (!nodeIds.Add(nodeId))
                {
                    throw new ScriptRuntimeException($"Duplicate script graph node id {nodeId}: {source}");
                }
                string type = RequiredString(node, "type", source);
                IReadOnlyDictionary<string, object?> values = node.TryGetProperty("values", out var valuesElement)
                    ? JsonValue.ReadObject(valuesElement, source)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                nodes.Add(new ScriptGraphNode(nodeId, type, values));
            }
            if (nodes.Count == 0)
            {
                throw new ScriptRuntimeException($"Script graph needs at least one node: {source}");
            }

            var edges = new List<ScriptGraphEdge>();
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement edge in edgesElement.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object)
                {
                    throw new ScriptRuntimeException($"Script graph edge must be an object: {source}");
                }
                string edgeId = RequiredString(edge, "id", source);
                if (!edgeIds.Add(edgeId))
                {
                    throw new ScriptRuntimeException($"Duplicate script graph edge id '{edgeId}': {source}");
                }
                JsonElement from = RequiredObject(edge, "from", source);
                JsonElement to = RequiredObject(edge, "to", source);
                int fromNode = RequiredInt(from, "node", source);
                int toNode = RequiredInt(to, "node", source);
                if (!nodeIds.Contains(fromNode) || !nodeIds.Contains(toNode))
                {
                    throw new ScriptRuntimeException($"Script graph edge '{edgeId}' references a missing node: {source}");
                }
                edges.Add(new ScriptGraphEdge(
                    edgeId,
                    fromNode,
                    RequiredString(from, "port", source),
                    toNode,
                    RequiredString(to, "port", source)
                ));
            }

            return new ScriptGraph(
                id,
                nodes.AsReadOnly(),
                edges.AsReadOnly(),
                JsonValue.ReadObject(root, source)
            );
        }
        catch (JsonException exception)
        {
            throw new ScriptRuntimeException($"Invalid script graph JSON '{source}': {exception.Message}", exception);
        }
    }

    private static JsonElement RequiredArray(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new ScriptRuntimeException($"Script graph '{source}' requires array '{name}'.");
        }
        return value;
    }

    private static JsonElement RequiredObject(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new ScriptRuntimeException($"Script graph '{source}' requires object '{name}'.");
        }
        return value;
    }

    private static string RequiredString(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ScriptRuntimeException($"Script graph '{source}' requires string '{name}'.");
        }
        string result = value.GetString()?.Trim() ?? string.Empty;
        if (result.Length == 0)
        {
            throw new ScriptRuntimeException($"Script graph '{source}' has empty '{name}'.");
        }
        if (result.Length > MaxIdentifierLength)
        {
            throw new ScriptRuntimeException(
                $"Script graph '{source}' has '{name}' longer than {MaxIdentifierLength} characters."
            );
        }
        return result;
    }

    private static int RequiredInt(JsonElement owner, string name, string source)
    {
        if (!owner.TryGetProperty(name, out var value) || !value.TryGetInt32(out int result))
        {
            throw new ScriptRuntimeException($"Script graph '{source}' requires integer '{name}'.");
        }
        return result;
    }

    private static class JsonValue
    {
        public static IReadOnlyDictionary<string, object?> ReadObject(JsonElement value, string source)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new ScriptRuntimeException($"Expected JSON object in script graph: {source}");
            }
            var result = value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => Read(property.Value, source),
                StringComparer.Ordinal
            );
            return new ReadOnlyDictionary<string, object?>(result);
        }

        private static object? Read(JsonElement value, string source)
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
                    value.EnumerateArray().Select(item => Read(item, source)).ToArray()
                ),
                JsonValueKind.Object => ReadObject(value, source),
                _ => throw new ScriptRuntimeException($"Unsupported JSON value in script graph: {source}"),
            };
        }
    }
}
