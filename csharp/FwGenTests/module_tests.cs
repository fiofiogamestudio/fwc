using System.Xml.Linq;
using static TestKit;

static class ModuleTests
{
    internal static TestCase[] Cases =>
    [
        new("C# kit dependency graph", TestProjectGraph),
        new("optional packages stay in their kits", TestPackageBounds),
        new("legacy aggregate is absent", TestNoLegacyAggregate),
    ];

    private static void TestProjectGraph()
    {
        var root = FrameworkRoot();
        var core = Project(root, "core/cs/Fw.Core.csproj");
        var anim = Project(root, "kit/anim/cs/Fw.Anim.csproj");
        var net = Project(root, "kit/net/cs/Fw.Net.csproj");
        var lite = Project(root, "kit/net/cs/lite/Fw.Net.Lite.csproj");
        var rec = Project(root, "kit/rec/cs/Fw.Rec.csproj");
        var ai = Project(root, "kit/ai/cs/Fw.AI.csproj");
        var lua = Project(root, "kit/lua/cs/Fw.Lua.csproj");
        var train = Project(root, "tool/train/cs/Fw.AI.Train.csproj");
        var e2e = Project(root, "tool/e2e/cs/Fw.Test.csproj");
        var gen = Project(root, "csharp/FwGen/FwGen.csproj");
        var tests = Project(root, "csharp/FwGenTests/FwGenTests.csproj");
        var verify = Project(root, "csharp/Fw.Verify/Fw.Verify.csproj");

        ExactRefs(core, []);
        ExactRefs(anim, [core]);
        ExactRefs(net, [core]);
        ExactRefs(lite, [net]);
        ExactRefs(rec, [core]);
        ExactRefs(ai, [core]);
        ExactRefs(lua, [core]);
        ExactRefs(train, [core, ai]);
        ExactRefs(e2e, [core, net]);
        ExactRefs(gen, [core]);
        ExactRefs(tests, [gen, core, anim, net, lite, rec, ai, lua, train, e2e]);
        ExactRefs(verify, [core, anim, net, lite, rec, ai, lua, train, e2e]);
    }

    private static void TestPackageBounds()
    {
        var root = FrameworkRoot();
        var packages = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => XDocument.Load(project)
                .Descendants("PackageReference")
                .Select(node => new
                {
                    Project = Path.GetFullPath(project),
                    Name = node.Attribute("Include")?.Value ?? "",
                }))
            .Where(item => item.Name is "LiteNetLib" or "MoonSharp")
            .ToArray();

        Equal(2, packages.Length, "optional package count");
        True(
            packages.Any(item => item.Name == "LiteNetLib"
                && item.Project == Project(root, "kit/net/cs/lite/Fw.Net.Lite.csproj")),
            "LiteNetLib stays in net adapter"
        );
        True(
            packages.Any(item => item.Name == "MoonSharp"
                && item.Project == Project(root, "kit/lua/cs/Fw.Lua.csproj")),
            "MoonSharp stays in lua kit"
        );
    }

    private static void TestNoLegacyAggregate()
    {
        var root = FrameworkRoot();
        True(
            !File.Exists(Path.Combine(root, "csharp", "FwRuntime", "FwRuntime.csproj")),
            "legacy aggregate project removed"
        );
        var references = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => XDocument.Load(project).Descendants("ProjectReference"))
            .Select(node => node.Attribute("Include")?.Value ?? "")
            .Where(value => value.Length > 0)
            .ToArray();
        True(
            !references.Any(value => value.Contains("FwRuntime", StringComparison.OrdinalIgnoreCase)),
            "no project references legacy aggregate"
        );
    }

    private static void ExactRefs(string project, IReadOnlyCollection<string> expected)
    {
        var directory = Path.GetDirectoryName(project)!;
        var actual = XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(node => node.Attribute("Include")?.Value ?? "")
            .Where(value => value.Length > 0)
            .Select(value => Path.GetFullPath(value.Replace('/', Path.DirectorySeparatorChar), directory))
            .ToHashSet(PathComparer());
        True(actual.SetEquals(expected.Select(Path.GetFullPath)), $"project refs: {Path.GetFileName(project)}");
    }

    private static string Project(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        True(File.Exists(path), $"project exists: {relative}");
        return path;
    }

    private static string FrameworkRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "core", "cs", "Fw.Core.csproj")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("cannot find fw root for module tests");
    }

    private static StringComparer PathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}
