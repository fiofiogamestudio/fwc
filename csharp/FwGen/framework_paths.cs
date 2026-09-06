using System.Text.RegularExpressions;

static class FrameworkPaths
{
    internal const string Default = "fwc";

    internal static string ValidateRelative(string value)
    {
        var relative = value.Replace('\\', '/');
        var parts = relative.Split('/');
        if (parts.Any(part => part is "" or "." or ".."
            || part.EndsWith(' ') || part.EndsWith('.')
            || !Regex.IsMatch(part, @"\A[A-Za-z0-9_. -]+\z")))
        {
            throw new InvalidOperationException("framework path must be a project-relative path using letters, digits, spaces, '_', '-' and '.', without traversal");
        }
        return relative;
    }

    internal static string ForScaffold(string root, string relative)
    {
        relative = ValidateRelative(relative);
        var current = Path.GetFullPath(root);
        var prefix = Path.TrimEndingDirectorySeparator(current) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            var directory = new DirectoryInfo(current);
            if (directory.Exists && directory.LinkTarget != null)
            {
                current = directory.ResolveLinkTarget(true)?.FullName
                    ?? throw new InvalidOperationException($"cannot resolve framework path link: {current}");
                if (!current.StartsWith(prefix, comparison))
                {
                    throw new InvalidOperationException("framework path link escapes project root");
                }
            }
        }
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static string FromConfig(string root, FwConfig config)
    {
        var project = config.GeneratorProjectPath(root);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(project)!); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "core", "cs", "Fw.Core.csproj"))
                && Directory.Exists(Path.Combine(directory.FullName, "kit")))
            {
                return directory.FullName;
            }
            if (directory.FullName.Equals(fullRoot, comparison)) break;
        }
        throw new DirectoryNotFoundException($"cannot find FWC root above configured generator project: {project}");
    }
}
