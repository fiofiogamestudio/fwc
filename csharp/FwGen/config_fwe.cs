using System.Text.Json;
using System.Text.Json.Serialization;

static class ConfigFwe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static void Stage(
        GenerationBatch batch,
        string root,
        FwConfig config,
        ConfigModel model
    )
    {
        var messages = new SortedDictionary<string, FweMessage>(StringComparer.Ordinal);
        foreach (var message in model.Messages.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            messages[message.Name] = new FweMessage
            {
                Fields = message.Fields.Select(field => Field(model.Schema, field)).ToArray(),
            };
        }

        var roots = new SortedDictionary<string, FweRoot>(StringComparer.Ordinal);
        foreach (var item in model.Roots)
        {
            roots[item.Name] = new FweRoot
            {
                Message = item.Message.Name,
                Source = item.SourcePath["res://".Length..],
                Format = item.IsJson ? "json" : "csv",
                Headers = item.IsJson
                    ? null
                    : item.Message.Fields.Select(field => field.Name).Prepend("key").ToArray(),
                Fields = item.Message.Fields.Select(field => Field(model.Schema, field)).ToArray(),
            };
        }

        var contract = new FweContract
        {
            SchemaHash = model.SchemaHash,
            Roots = roots,
            Messages = messages,
        };
        batch.StageText(
            config.ConfigFwePath(root),
            JsonSerializer.Serialize(contract, JsonOptions) + "\n"
        );
    }

    private static FweField Field(ProtoSchema schema, ProtoField field)
    {
        var isMessage = schema.Messages.ContainsKey(field.Type) && field.Type != "Fixed32";
        return new FweField
        {
            Name = field.Name,
            ProtoType = field.Type,
            EditorType = EditorType(field, isMessage),
            Repeated = field.IsRepeated,
            Reference = isMessage && ConfigSchema.IsConfigMessage(field.Type)
                ? ConfigSchema.ConfigRootName(field.Type)
                : null,
            ObjectType = isMessage && !ConfigSchema.IsConfigMessage(field.Type)
                ? field.Type
                : null,
        };
    }

    private static string EditorType(ProtoField field, bool isMessage)
    {
        if (field.IsRepeated)
        {
            return "array";
        }
        if (isMessage)
        {
            return ConfigSchema.IsConfigMessage(field.Type) ? "reference" : "object";
        }
        return field.Type switch
        {
            "bool" => "bool",
            "float" or "double" or "Fixed32" => "number",
            "int32" or "int64" or "uint32" or "uint64" or "sint32" or "sint64" => "int",
            _ => "string",
        };
    }

    private sealed class FweContract
    {
        public int Format { get; init; } = 1;
        public required string SchemaHash { get; init; }
        public required SortedDictionary<string, FweRoot> Roots { get; init; }
        public required SortedDictionary<string, FweMessage> Messages { get; init; }
    }

    private sealed class FweRoot
    {
        public required string Message { get; init; }
        public required string Source { get; init; }
        public required string Format { get; init; }
        public string[]? Headers { get; init; }
        public required FweField[] Fields { get; init; }
    }

    private sealed class FweMessage
    {
        public required FweField[] Fields { get; init; }
    }

    private sealed class FweField
    {
        public required string Name { get; init; }
        public required string ProtoType { get; init; }
        public required string EditorType { get; init; }
        public bool Repeated { get; init; }
        public string? Reference { get; init; }
        public string? ObjectType { get; init; }
    }
}
