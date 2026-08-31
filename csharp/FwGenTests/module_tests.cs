using System.Reflection;
using System.Xml.Linq;
using static TestKit;

static class ModuleTests
{
    internal static TestCase[] Cases =>
    [
        new("C# kit dependency graph", TestProjectGraph),
        new("optional packages stay in their kits", TestPackageBounds),
        new("compat aggregate forwards public types", TestCompatForwarders),
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

        ExactRefs(core, []);
        ExactRefs(anim, [core]);
        ExactRefs(net, [core]);
        ExactRefs(lite, [net]);
        ExactRefs(rec, [core]);
        ExactRefs(ai, [core]);
        ExactRefs(lua, [core]);
        ExactRefs(train, [core, ai]);
        ExactRefs(e2e, [core, net]);

        var runtime = Project(root, "csharp/FwRuntime/FwRuntime.csproj");
        var runtimeText = File.ReadAllText(runtime);
        True(runtimeText.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", StringComparison.Ordinal), "compat runtime has no sources");
        ExactRefs(runtime, [core, anim, net, lite, rec, ai, lua, train, e2e]);
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

    private static void TestCompatForwarders()
    {
        var moduleNames = new[]
        {
            "Fw.Core",
            "Fw.Anim",
            "Fw.Net",
            "Fw.Net.Lite",
            "Fw.Rec",
            "Fw.AI",
            "Fw.Lua",
            "Fw.AI.Train",
            "Fw.Test",
        };
        var expected = moduleNames
            .Select(name => Assembly.Load(new AssemblyName(name)))
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.Namespace?.StartsWith("Fw.Rt.", StringComparison.Ordinal) == true)
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        var compat = Assembly.Load(new AssemblyName("FwRuntime"));
        var forwarded = compat.GetForwardedTypes()
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        True(forwarded.SetEquals(expected), "compat forwards every public Fw.Rt type");
        var resolved = Type.GetType("Fw.Rt.Systems.SystemRuntime, FwRuntime", throwOnError: false);
        True(resolved?.Assembly.GetName().Name == "Fw.Core", "legacy assembly-qualified type resolves");
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
