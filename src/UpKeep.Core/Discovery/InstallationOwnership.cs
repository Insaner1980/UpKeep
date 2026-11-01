using UpKeep.Core.Models;

namespace UpKeep.Core.Discovery;

public static class InstallationOwnership
{
    public static IReadOnlyList<UpdateItem> Resolve(IEnumerable<UpdateItem> items)
    {
        var inventory = items.ToArray();
        // A built-in updater must not compete with the registered installer for the same root.
        items = inventory.Select(item => ResolveUpdater(item, inventory));
        var results = new List<UpdateItem>();
        foreach (var group in items.GroupBy(item => item.Key.EnvironmentId.StartsWith("WSL:", StringComparison.Ordinal)
            ? item.Key.EnvironmentId + "|" + item.Key.InstallIdentity
            : item.Key.EnvironmentId.ToUpperInvariant() + "|" + item.Key.InstallIdentity.ToUpperInvariant()))
        {
            var entries = group.DistinctBy(item => item.Key).ToArray();
            if (entries.Length == 1)
            {
                results.Add(entries[0]);
                continue;
            }
            var owner = entries.Where(i => i.Key.ProviderId == "winget").ToArray();
            if (owner.Length == 1 && entries.All(i => i.Key.ProviderId is "winget" or "native"))
            {
                results.Add(owner[0]);
            }
            else
            {
                results.AddRange(entries.Select(i => i with { State = UpdateState.Blocked, Detail = "Installation source unclear; multiple update owners resolve to this installation." }));
            }
        }
        return results;
    }
    public static bool IsLinuxInstallation(string path) => path.StartsWith('/') && !path.StartsWith("/mnt/", StringComparison.Ordinal) && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public static bool IsAvailableInterpreter(string path) => Path.IsPathFullyQualified(path) && ToolLocator.IsHostPath(path) && File.Exists(path);
    private static UpdateItem ResolveUpdater(UpdateItem item, IReadOnlyList<UpdateItem> inventory)
    {

        if (item.Kind != UpdateKind.RunUpdater || item.Metadata is not { } metadata)
        {
            return item;
        }

        if (metadata.Manager is ManagerKind.CodeExtension or ManagerKind.DotNetTool or ManagerKind.Python || metadata.Manager == ManagerKind.Bun && item.Key.PackageId != "Bun")
        {
            return item;
        }

        var owners = inventory.Where(i => i.Key.ProviderId == "winget" && Path.IsPathFullyQualified(i.Key.InstallIdentity) &&
            metadata.Tool.Executable.StartsWith(Path.TrimEndingDirectorySeparator(i.Key.InstallIdentity) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return owners.Any() ? item with
        {
            State = UpdateState.Blocked,
            Detail = "This installation is owned by the matching WinGet package. Use that update row."
        } : item;
    }
}
