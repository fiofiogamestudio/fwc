using System.Text;

static class KitSync
{
    private sealed record ProjectSpec(string Kit, string Path);
    private sealed record SyncFile(string Path, string Text);
    private sealed record SyncPlan(
        string ProjectionRoot,
        string SourceIgnore,
        IReadOnlyList<string> Inputs,
        IReadOnlyList<SyncFile> Files
    );

    private static readonly ProjectSpec[] Projects =
    [
        new("anim", "kit/anim/cs/Fw.Anim.csproj"),
        new("net", "kit/net/cs/Fw.Net.csproj"),
        new("net", "kit/net/cs/lite/Fw.Net.Lite.csproj"),
        new("rec", "kit/rec/cs/Fw.Rec.csproj"),
        new("ai", "kit/ai/cs/Fw.AI.csproj"),
        new("lua", "kit/lua/cs/Fw.Lua.csproj"),
    ];

    public static void Run(string root, FwConfig config)
    {
        if (!config.HasUseSection())
        {
            CleanCompat(root, config);
            Console.WriteLine("fw sync skipped: [use] is not set (compat mode)");
            return;
        }

        var plan = CreatePlan(root, config);
        var batch = new GenerationBatch(root);
        foreach (var file in plan.Files)
        {
            batch.StageText(file.Path, file.Text);
        }

        var outputs = plan.Files.Select(file => Path.GetFullPath(file.Path)).ToHashSet(PathComparer());
        StageRecordedDeletes(batch, root, config, outputs);
        if (Directory.Exists(plan.ProjectionRoot))
        {
            foreach (var path in Directory.GetFiles(plan.ProjectionRoot, "*", SearchOption.AllDirectories))
            {
                if (!outputs.Contains(Path.GetFullPath(path)))
                {
                    batch.StageDelete(path);
                }
            }
        }
        GenerationManifest.StageSync(batch, root, config, plan.Inputs, outputs);
        batch.Commit();
        Console.WriteLine(
            $"synced fw kits: game=[{string.Join(",", config.GameKits())}] host=[{string.Join(",", config.HostKits())}]"
        );
    }

    internal static IReadOnlyList<string> Inputs(string root, FwConfig config)
    {
        return CreatePlan(root, config).Inputs;
    }

    internal static IReadOnlyList<string> Outputs(string root, FwConfig config)
    {
        return CreatePlan(root, config).Files.Select(file => file.Path).ToArray();
    }

    private static SyncPlan CreatePlan(string root, FwConfig config)
    {
        var fwRoot = ResolveFwRoot(root, config);
        var inputs = new HashSet<string>(PathComparer())
        {
            Path.Combine(root, "fw.toml"),
            Path.Combine(fwRoot, "Directory.Build.props"),
        };
        var files = new List<SyncFile>();

        AddProps(files, inputs, fwRoot, config.GameKitPropsPath(root), config.GameKits());
        AddProps(files, inputs, fwRoot, config.HostKitPropsPath(root), config.HostKits());

        var projectionRoot = config.GodotFwDir(root);
        var sourceRoot = Path.Combine(fwRoot, "scripts", "fw");
        var sourceIgnore = Path.Combine(fwRoot, "scripts", ".gdignore");
        var game = config.GameKits().ToHashSet(StringComparer.Ordinal);
        var targetPrefix = "res://" + Path.GetRelativePath(root, Path.Combine(projectionRoot, "fw"))
            .Replace('\\', '/');
        if (Directory.Exists(sourceRoot))
        {
            foreach (var source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(sourceRoot, source);
                var normalized = relative.Replace('\\', '/');
                var isAnim = normalized.StartsWith("vu/animation/", StringComparison.Ordinal);
                if ((isAnim && !game.Contains("anim")) || (!isAnim && !game.Contains("app")))
                {
                    continue;
                }
                if (!source.EndsWith(".gd", StringComparison.OrdinalIgnoreCase)
                    && !source.EndsWith(".uid", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                inputs.Add(source);
                var text = File.ReadAllText(source, Encoding.UTF8)
                    .Replace("res://fw/scripts/fw", targetPrefix, StringComparison.Ordinal);
                files.Add(new SyncFile(Path.Combine(projectionRoot, "fw", relative), text));
            }
        }
        files.Add(new SyncFile(sourceIgnore, ""));

        return new SyncPlan(
            projectionRoot,
            sourceIgnore,
            inputs.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()
        );
    }

    private static void CleanCompat(string root, FwConfig config)
    {
        var fwRoot = ResolveFwRoot(root, config);
        var batch = new GenerationBatch(root);
        StageRecordedDeletes(batch, root, config, new HashSet<string>(PathComparer()));
        var projectionRoot = config.GodotFwDir(root);
        if (Directory.Exists(projectionRoot))
        {
            foreach (var path in Directory.GetFiles(projectionRoot, "*", SearchOption.AllDirectories))
            {
                batch.StageDelete(path);
            }
        }
        batch.StageDelete(config.GameKitPropsPath(root));
        batch.StageDelete(config.HostKitPropsPath(root));
        batch.StageDelete(Path.Combine(fwRoot, "scripts", ".gdignore"));
        batch.Commit();
    }

    private static void StageRecordedDeletes(
        GenerationBatch batch,
        string root,
        FwConfig config,
        IReadOnlySet<string> keep
    )
    {
        foreach (var path in GenerationManifest.RecordedOutputs(root, config, "sync"))
        {
            if (!keep.Contains(path) && IsManagedSyncOutput(root, path))
            {
                batch.StageDelete(path);
            }
        }
    }

    private static bool IsManagedSyncOutput(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path))
            .Replace('\\', '/');
        var name = Path.GetFileName(path);
        if (name is "_fw_game.props" or "_fw_host.props")
        {
            return true;
        }
        if (name == ".gdignore" && relative.EndsWith("/scripts/.gdignore", StringComparison.Ordinal))
        {
            return true;
        }
        var projection = relative.StartsWith("_fw/fw/", StringComparison.Ordinal)
            || relative.Contains("/_fw/fw/", StringComparison.Ordinal);
        return projection
            && name.StartsWith("_", StringComparison.Ordinal)
            && (path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".uid", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddProps(
        List<SyncFile> files,
        HashSet<string> inputs,
        string fwRoot,
        string output,
        IReadOnlyList<string> kits
    )
    {
        var selected = kits.ToHashSet(StringComparer.Ordinal);
        var projectPaths = new List<string> { Path.Combine(fwRoot, "core", "cs", "Fw.Core.csproj") };
        projectPaths.AddRange(Projects
            .Where(project => selected.Contains(project.Kit))
            .Select(project => Path.Combine(fwRoot, project.Path.Replace('/', Path.DirectorySeparatorChar))));

        foreach (var path in projectPaths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"fw kit project not found: {path}");
            }
            inputs.Add(path);
        }

        var directory = Path.GetDirectoryName(output)
            ?? throw new InvalidOperationException($"fw props output has no directory: {output}");
        var text = new StringBuilder();
        text.AppendLine("<Project>");
        text.AppendLine("  <ItemGroup>");
        foreach (var path in projectPaths)
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            text.AppendLine($"    <ProjectReference Include=\"$(MSBuildThisFileDirectory){Xml(relative)}\" />");
        }
        text.AppendLine("  </ItemGroup>");
        text.AppendLine("</Project>");
        files.Add(new SyncFile(output, text.ToString()));
    }

    private static string ResolveFwRoot(string root, FwConfig config)
    {
        var project = config.GeneratorProjectPath(root);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(project)!); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "core", "cs", "Fw.Core.csproj"))
                && Directory.Exists(Path.Combine(directory.FullName, "kit")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException($"cannot find fw root above generator project: {project}");
    }

    private static string Xml(string value)
    {
        return value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static StringComparer PathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}
