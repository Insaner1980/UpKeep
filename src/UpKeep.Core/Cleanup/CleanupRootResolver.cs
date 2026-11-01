using UpKeep.Core.Models;

namespace UpKeep.Core.Cleanup;

public static class CleanupRootResolver
{
    private static readonly string[] TemporaryFolderNames = ["tmp", "Temp"];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S5443", Justification = "Discovers existing temporary files for explicit reviewed cleanup; creates no files in the shared directory. PathGuard revalidates identity and excludes reparse points before removal.")]
    public static IReadOnlyList<CleanupRoot> Resolve(IReadOnlyList<string> projectRoots)
    {
        var roots = new List<CleanupRoot>();
        void Add(string? path, RootKind kind, CleanupCategory category) => AddRoot(roots, path, kind, category);
        Add(Path.GetTempPath(), RootKind.Temporary, CleanupCategory.TemporaryFiles);
        Add(Environment.GetEnvironmentVariable("TMP"), RootKind.Temporary, CleanupCategory.TemporaryFiles);
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        foreach (var path in TemporaryFolderNames.Select(name => Path.Combine(drive, name)).Where(Directory.Exists))
        {
            Add(path, RootKind.Temporary, CleanupCategory.TemporaryFiles);
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var relative in new[] { "Temp", "SystemTemp", @"ServiceProfiles\LocalService\AppData\Local\Temp", @"ServiceProfiles\NetworkService\AppData\Local\Temp" })
        {
            Add(Path.Combine(windows, relative), RootKind.ProtectedSystem, CleanupCategory.TemporaryFiles);
        }

        foreach (var path in projectRoots)
        {
            Add(path, RootKind.Project, CleanupCategory.TestArtifacts);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        foreach (var path in new[] { Path.Combine(codex, "log"), Path.Combine(local, "CrashDumps") }.Where(Directory.Exists))
        {
            Add(path, RootKind.Logs, CleanupCategory.OldLogs);
        }
        foreach (var path in new[] { Path.Combine(home, ".cache", "codex-runtimes"), Path.Combine(home, ".sonar", "cache"), Path.Combine(home, ".m2", "repository"), Environment.GetEnvironmentVariable("GRADLE_USER_HOME") ?? Path.Combine(home, ".gradle"), Path.Combine(local, "ms-playwright") }.Where(Directory.Exists))
        {
            Add(path, RootKind.ReviewOnly, CleanupCategory.PackageCaches);
        }

        foreach (var basePath in new[] { Path.Combine(local, "Codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Codex") }.Where(Directory.Exists))
        {
            Add(basePath, RootKind.ReviewOnly, CleanupCategory.PackageCaches);
        }

        AddCodexPackages(local, Add);
        var google = Path.Combine(local, "Google");
        if (!Directory.Exists(google))
        {
            return roots;
        }

        foreach (var ide in Directory.EnumerateDirectories(google, "AndroidStudio*", SearchOption.TopDirectoryOnly))
        {
            Add(ide, RootKind.ReviewOnly, CleanupCategory.PackageCaches);
            if (Directory.Exists(Path.Combine(ide, "log")))
            {
                Add(Path.Combine(ide, "log"), RootKind.Logs, CleanupCategory.OldLogs);
            }
        }
        return roots;
    }
    private static void AddRoot(List<CleanupRoot> roots, string? path, RootKind kind, CleanupCategory category)
    {

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = Path.TrimEndingDirectorySeparator(path);
        if (PathGuard.ValidLocalRoot(path) && !roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            roots.Add(new(Guid.NewGuid().ToString("N"), path, kind, category));
        }
    }
    private static void AddCodexPackages(string local, Action<string?, RootKind, CleanupCategory> add)
    {
        try
        {
            foreach (var package in new Windows.Management.Deployment.PackageManager().FindPackagesForUser("").Where(package => package.Id.Name.Contains("Codex", StringComparison.OrdinalIgnoreCase)))
            {
                add(Path.Combine(local, "Packages", package.Id.FamilyName, "LocalCache"), RootKind.ReviewOnly, CleanupCategory.PackageCaches);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { // Package enumeration is optional; other cleanup roots remain available.
        }
    }
}
