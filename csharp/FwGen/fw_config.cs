using System.Text;
using System.Text.RegularExpressions;

sealed class FwConfig
{
    private static readonly string[] KitIds = ["app", "anim", "net", "rec", "ai", "lua"];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedKeys =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["project"] = new(StringComparer.Ordinal) { "name" },
            ["schema"] = new(StringComparer.Ordinal) { "system", "bridge", "config" },
            ["gen"] = new(StringComparer.Ordinal) { "gdscript", "csharp", "fwe" },
            ["data"] = new(StringComparer.Ordinal) { "config" },
            ["pack"] = new(StringComparer.Ordinal) { "config" },
            ["script"] = new(StringComparer.Ordinal) { "gdscript", "csharp" },
            ["dotnet"] = new(StringComparer.Ordinal) { "game", "host", "fwgen" },
            ["use"] = new(StringComparer.Ordinal) { "game", "host", "game_net_adapter", "host_net_adapter" },
        };

    private readonly Dictionary<string, Dictionary<string, string>> _sections;
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<string>>> _lists;

    private FwConfig(
        Dictionary<string, Dictionary<string, string>> sections,
        Dictionary<string, Dictionary<string, IReadOnlyList<string>>> lists
    )
    {
        _sections = sections;
        _lists = lists;
    }

    public string Value(string section, string key, string fallback)
    {
        if (_sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value))
        {
            return value;
        }
        return fallback;
    }

    public bool HasValue(string section, string key)
    {
        return _sections.TryGetValue(section, out var values) && values.ContainsKey(key);
    }

    public bool HasValues(string section, string key)
    {
        return _lists.TryGetValue(section, out var values) && values.ContainsKey(key);
    }

    public IReadOnlyList<string> Values(string section, string key, IReadOnlyList<string> fallback)
    {
        if (_lists.TryGetValue(section, out var values) && values.TryGetValue(key, out var value))
        {
            return value;
        }
        return fallback;
    }

    public bool HasUseSection()
    {
        return _lists.ContainsKey("use");
    }

    public IReadOnlyList<string> GameKits()
    {
        return Values("use", "game", []);
    }

    public IReadOnlyList<string> HostKits()
    {
        return Values("use", "host", []);
    }

    public string NetAdapter(string target)
    {
        return Value("use", $"{target}_net_adapter", "lite");
    }

    public string PathValue(string root, string section, string key, string fallback)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, Value(section, key, fallback)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!path.Equals(fullRoot, comparison)
            && !path.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException($"fw.toml [{section}].{key} escapes project root: {Value(section, key, fallback)}");
        }
        return path;
    }

    public string ProjectName()
    {
        return Value("project", "name", "Game");
    }

    public string CSharpProjectPath(string root)
    {
        return PathValue(root, "dotnet", "game", $"{ProjectName()}.csproj");
    }

    public string GeneratorProjectPath(string root)
    {
        return PathValue(root, "dotnet", "fwgen", "fw/csharp/FwGen/FwGen.csproj");
    }

    public bool HasHostProject()
    {
        return HasValue("dotnet", "host");
    }

    public string HostProjectPath(string root)
    {
        return PathValue(root, "dotnet", "host", "Host.csproj");
    }

    public string BridgeSchemaDir(string root)
    {
        return PathValue(root, "schema", "bridge", "schema/bridge");
    }

    public string ConfigSchemaDir(string root)
    {
        return PathValue(root, "schema", "config", "schema/config");
    }

    public string ConfigDataDir(string root)
    {
        return PathValue(root, "data", "config", "data/config");
    }

    public string SystemsSchemaPath(string root)
    {
        return PathValue(root, "schema", "system", "schema/systems.toml");
    }

    public string GodotGenDir(string root)
    {
        return PathValue(root, "gen", "gdscript", "scripts/_gen");
    }

    public string GodotFwDir(string root)
    {
        return Path.GetFullPath(Path.Combine(ScriptGdDir(root), "_fw"));
    }

    public string GodotSystemsGdPath(string root)
    {
        return Path.GetFullPath(Path.Combine(GodotGenDir(root), "_godot_systems.gd"));
    }

    public string ConfigGdPath(string root)
    {
        return Path.GetFullPath(Path.Combine(GodotGenDir(root), "_config.gd"));
    }

    public bool HasFweGen()
    {
        return HasValue("gen", "fwe");
    }

    public string FweGenDir(string root)
    {
        return PathValue(root, "gen", "fwe", "tools/fwe/_gen");
    }

    public string ConfigFwePath(string root)
    {
        return Path.GetFullPath(Path.Combine(FweGenDir(root), "_config_schema.json"));
    }

    public string ConfigPackDir(string root)
    {
        return PathValue(root, "pack", "config", "pack/config");
    }

    public string CoreSystemsCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_core_systems.cs"));
    }

    public string BridgeTypesCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_bridge_types.cs"));
    }

    public string BridgeCodecCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_bridge_codec.cs"));
    }

    public string BridgeIntentCodecCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_intent_codec.cs"));
    }

    public string BridgeEventCodecCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_event_codec.cs"));
    }

    public string BridgePacketCodecCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_packet_codec.cs"));
    }

    public string ConfigContractCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_config_contract.cs"));
    }

    public string ConfigCodecCsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_config_codec.cs"));
    }

    public string GenerationManifestPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenRoot(root), "_fwgen_manifest.json"));
    }

    public string GameKitPropsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenDir(root), "_fw_game.props"));
    }

    public string HostKitPropsPath(string root)
    {
        return Path.GetFullPath(Path.Combine(CSharpGenDir(root), "_fw_host.props"));
    }

    public static FwConfig Load(string root)
    {
        var path = Path.Combine(root, "fw.toml");
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var lists = new Dictionary<string, Dictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return new FwConfig(sections, lists);
        }

        var section = "";
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNo = index + 1;
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var sectionMatch = Regex.Match(line, @"^\[(.+)\]$");
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups[1].Value.Trim();
                if (!AllowedKeys.ContainsKey(section))
                {
                    throw new InvalidOperationException($"{path}:{lineNo} unsupported fw.toml section [{section}]");
                }
                if (!sections.TryAdd(section, new Dictionary<string, string>(StringComparer.Ordinal)))
                {
                    throw new InvalidOperationException($"{path}:{lineNo} duplicate fw.toml section [{section}]");
                }
                lists.Add(section, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
                continue;
            }

            var assignmentMatch = Regex.Match(line, @"^([A-Za-z0-9_]+)\s*=\s*(.+)$");
            if (!assignmentMatch.Success || section.Length == 0)
            {
                throw new InvalidOperationException($"{path}:{lineNo} expected an assignment under a known section");
            }
            var key = assignmentMatch.Groups[1].Value;
            if (!AllowedKeys[section].Contains(key))
            {
                throw new InvalidOperationException($"{path}:{lineNo} unsupported fw.toml key [{section}].{key}");
            }
            if (sections[section].ContainsKey(key) || lists[section].ContainsKey(key))
            {
                throw new InvalidOperationException($"{path}:{lineNo} duplicate fw.toml key [{section}].{key}");
            }

            var raw = assignmentMatch.Groups[2].Value.Trim();
            if (raw.StartsWith("[", StringComparison.Ordinal))
            {
                if (!string.Equals(section, "use", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{path}:{lineNo} arrays are only supported under [use]");
                }
                lists[section].Add(key, ParseStringArray(path, lineNo, raw));
                continue;
            }

            var valueMatch = Regex.Match(raw, "^\"([^\"]*)\"$");
            if (!valueMatch.Success)
            {
                throw new InvalidOperationException($"{path}:{lineNo} expected `key = \"value\"`");
            }
            sections[section].Add(key, valueMatch.Groups[1].Value);
        }

        var config = new FwConfig(sections, lists);
        if (!config.HasUseSection())
        {
            throw new InvalidOperationException($"{path} missing required [use] section");
        }
        foreach (var key in new[] { "game", "host" })
        {
            if (!config.HasValues("use", key))
            {
                throw new InvalidOperationException($"{path} missing [use].{key} string array");
            }
        }
        config.ValidateKitList(path, "game");
        config.ValidateKitList(path, "host");
        config.ValidateNetAdapter(path, "game");
        config.ValidateNetAdapter(path, "host");
        return config;
    }

    private void ValidateNetAdapter(string path, string target)
    {
        string key = $"{target}_net_adapter";
        if (HasValues("use", key))
        {
            throw new InvalidOperationException($"{path} [use].{key} must be a string: lite or none");
        }
        if (!HasValue("use", key)) return;
        if (NetAdapter(target) is not ("lite" or "none"))
        {
            throw new InvalidOperationException($"{path} [use].{key} must be lite or none");
        }
        if (!Values("use", target, []).Contains("net", StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{path} [use].{key} requires net in [use].{target}");
        }
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuote = !inQuote;
            }
            if (!inQuote && line[i] == '#')
            {
                return line[..i];
            }
        }
        return line;
    }

    private string CSharpGenRoot(string root)
    {
        return PathValue(root, "gen", "csharp", "csharp/_gen");
    }

    private string CSharpGenDir(string root)
    {
        return CSharpGenRoot(root);
    }

    private string ScriptGdDir(string root)
    {
        return PathValue(root, "script", "gdscript", "scripts");
    }

    private void ValidateKitList(string path, string key)
    {
        if (!_lists.TryGetValue("use", out var use) || !use.TryGetValue(key, out var values))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.Equals(value, "core", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{path} [use].{key} must not list core; core is automatic");
            }
            if (!KitIds.Contains(value, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"{path} [use].{key} has unknown kit `{value}`");
            }
            if (string.Equals(key, "host", StringComparison.Ordinal)
                && string.Equals(value, "app", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{path} [use].host cannot use app; app is projected only for game");
            }
            if (!seen.Add(value))
            {
                throw new InvalidOperationException($"{path} [use].{key} repeats kit `{value}`");
            }
        }
    }

    private static IReadOnlyList<string> ParseStringArray(string path, int lineNo, string raw)
    {
        if (!raw.EndsWith("]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{path}:{lineNo} unterminated string array");
        }

        var values = new List<string>();
        var inner = raw[1..^1];
        var index = 0;
        while (true)
        {
            SkipSpace(inner, ref index);
            if (index == inner.Length)
            {
                return values.AsReadOnly();
            }
            if (inner[index] != '"')
            {
                throw new InvalidOperationException($"{path}:{lineNo} [use] values must be quoted strings");
            }

            var end = inner.IndexOf('"', index + 1);
            if (end < 0)
            {
                throw new InvalidOperationException($"{path}:{lineNo} unterminated string in array");
            }
            var value = inner[(index + 1)..end];
            if (value.Length == 0)
            {
                throw new InvalidOperationException($"{path}:{lineNo} [use] kit id cannot be empty");
            }
            values.Add(value);
            index = end + 1;
            SkipSpace(inner, ref index);
            if (index == inner.Length)
            {
                return values.AsReadOnly();
            }
            if (inner[index] != ',')
            {
                throw new InvalidOperationException($"{path}:{lineNo} expected comma between [use] kits");
            }
            index += 1;
            SkipSpace(inner, ref index);
            if (index == inner.Length)
            {
                throw new InvalidOperationException($"{path}:{lineNo} trailing comma is not supported in [use]");
            }
        }
    }

    private static void SkipSpace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index += 1;
        }
    }
}
