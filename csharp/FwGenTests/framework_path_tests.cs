using static TestKit;

static class FrameworkPathTests
{
    internal static TestCase[] Cases =>
    [
        new("framework path default and traversal rejection", TestPaths),
        new("canonical fwc scaffold and relocated paths", TestScaffoldMatrix),
    ];

    private static void TestPaths()
    {
        Equal("fwc", CliOptions.Parse(["craft", "fw-new"]).FrameworkPath, "canonical default");
        Equal("modules/code kit", CliOptions.Parse(["--framework-path", "modules\\code kit"]).FrameworkPath, "portable separators");
        foreach (var path in new[] { "", ".", "..", "../fwc", "fwc/../other", "/fwc", "C:/fwc", "a//b", "a/", "a./b", "a /b", "a\"b", "a$b", "a`b", "a;b", "a\nb", "a\n" })
        {
            Throws(() => CliOptions.Parse(["--framework-path", path]), "framework path");
        }
        WithTempDir(root =>
        {
            Throws(() => Craft.Run(root, FwConfig.Load(root), ["fw-new"],
                CliOptions.Parse(["--framework-path", "../fwc"])), "framework path");
            Equal(0, Directory.GetFileSystemEntries(root).Length, "invalid path writes no scaffold");
        });
    }

    private static void TestScaffoldMatrix()
    {
        var framework = FindFramework();
        foreach (var relative in new[] { "fwc", "modules/code kit" })
        {
            WithTempDir(root =>
            {
                var installed = Path.Combine(root, relative);
                foreach (var sourceRoot in new[] { "templates", "scripts", "core", "kit", "csharp/FwGen" })
                {
                    foreach (var source in Directory.EnumerateFiles(Path.Combine(framework, sourceRoot), "*", SearchOption.AllDirectories))
                    {
                        var sourceRelative = Path.GetRelativePath(framework, source).Replace('\\', '/');
                        if (sourceRelative.Split('/').Any(part => part is "bin" or "obj" or ".godot")) continue;
                        Write(installed, sourceRelative, File.ReadAllText(source));
                    }
                }
                foreach (var file in new[] { "Directory.Build.props", "csharp/Directory.Build.props" })
                {
                    Write(installed, file, File.ReadAllText(Path.Combine(framework, file)));
                }
                var options = relative == "fwc"
                    ? CliOptions.Parse(["--name", "PathProbe"])
                    : CliOptions.Parse(["--name", "PathProbe", "--framework-path", relative]);
                Craft.Run(root, FwConfig.Load(root), ["fw-new"], options);
                var config = FwConfig.Load(root);
                Equal(Path.GetFullPath(Path.Combine(installed, "csharp", "FwGen", "FwGen.csproj")), config.GeneratorProjectPath(root), "configured generator follows route");
                True(File.ReadAllText(Path.Combine(root, "justfile")).Contains($"./{relative}/tools/", StringComparison.Ordinal), "commands follow route");
                True(!Directory.Exists(Path.Combine(root, "fw")), "does not create duplicate framework");
                True(File.ReadAllText(config.GameKitPropsPath(root)).Contains(relative, StringComparison.Ordinal), "kit references follow route");
                var projected = Path.Combine(root, "scripts", "_fw", "fw", "rt", "system", "_app_root.gd");
                True(File.Exists(projected), "projection API path stays stable");
                var manifestBefore = File.ReadAllText(config.GenerationManifestPath(root));
                Craft.Run(root, config, ["fw-new"], options);
                Equal(manifestBefore, File.ReadAllText(config.GenerationManifestPath(root)), "repeated scaffold deterministic");
                File.AppendAllText(Path.Combine(installed, "csharp", "FwGen", "craft.cs"), "\n// route hash probe\n");
                Throws(() => GenerationManifest.Verify(root, config), "different fwgen build");
            });
        }
    }

    private static string FindFramework()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "core", "cs", "Fw.Core.csproj"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("cannot find FWC for path regression fixtures");
    }
}
