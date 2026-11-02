using System.Diagnostics;
using System.Text.Json;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Updates;

namespace UpKeep.Core.Cleanup;

public sealed record ManagedCleanupAction(string Id, string Name, CleanupCategory Category, ToolEntry Tool, IReadOnlyList<string> Arguments, string Location, string Detail, Eligibility Eligibility, string? AvdName = null, IReadOnlyList<string>? LocationQuery = null, FileIdentity? AvdIdentity = null);
public sealed class ManagedActions(DiscoveryContext context, Func<bool>? emulatorRunning = null, Func<string>? avdRoot = null)
{
    private const string EmulatorCommand = "emulator";
    private const string CacheCommand = "cache";
    private readonly Func<bool> emulatorRunning = emulatorRunning ?? AnyEmulatorRunning;
    private readonly Func<string> avdRoot = avdRoot ?? (() => Environment.GetEnvironmentVariable("ANDROID_AVD_HOME") ?? Path.Combine(Environment.GetEnvironmentVariable("ANDROID_EMULATOR_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_USER_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android"), "avd"));
    private CancellationToken queryCancellation;
    private readonly Dictionary<string, ManagedCleanupAction> reviewed = [];
    private async Task<string> Query(ToolEntry tool, params string[] arguments)
    {
        queryCancellation.ThrowIfCancellationRequested();
        var r = await context.Runner.RunAsync(new(tool.Executable, tool.PrefixArguments.Concat(arguments).ToArray(), context.WorkingDirectory, Environment: tool.Environment), null, queryCancellation);
        queryCancellation.ThrowIfCancellationRequested();
        if (!r.Success)
        {
            throw new IOException("Location query failed or was incomplete.");
        }

        return ProviderParser.Normalize(r.StandardOutput).Trim();
    }
    public static bool AnyEmulatorRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Contains("qemu-system", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals(EmulatorCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException) { return true; }
            }
        }
        return false;
    }
    public async Task<IReadOnlyList<ManagedCleanupAction>> DiscoverAsync(CancellationToken token)
    {
        queryCancellation = token;
        var result = new List<ManagedCleanupAction>();
        async Task Cache(string name, string label, string[] query, string[] action)
        {
            var tool = context.Tools.Find(name);
            if (tool is null)
            {
                return;
            }

            token.ThrowIfCancellationRequested();
            try
            {
                var root = await Query(tool, query);
                if (!PathGuard.ValidLocalRoot(root))
                {
                    throw new IOException();
                }

                result.Add(new(name + "-cache", label, CleanupCategory.PackageCaches, tool, action, root, "Manager action; exact removed files and reclaimed bytes are not known. Future installs may download again.", Eligibility.ReviewRequired, LocationQuery: query));
            }
            catch (IOException) { result.Add(new(name + "-cache", label, CleanupCategory.PackageCaches, tool, [], "Unknown", "Cache location unavailable; no removal is enabled.", Eligibility.Unavailable)); }
        }
        await Cache("pnpm", "Prune unused pnpm packages", ["store", "path"], ["store", "prune"]);
        await Cache("uv", "Prune uv cache", [CacheCommand, "dir"], [CacheCommand, "prune"]);
        await Cache("bun", "Prune old Bun cache", ["pm", CacheCommand], ["pm", CacheCommand, "prune"]);
        await DiscoverNuGetAsync(result);
        await DiscoverNpmAsync(result);
        await DiscoverEmulatorsAsync(result);
        await DiscoverPipAsync(result);
        reviewed.Clear();
        foreach (var action in result)
        {
            reviewed[action.Id] = action;
        }

        return result;
    }
    public async Task<CleanupItemResult> ExecuteAsync(ManagedCleanupAction action, CancellationToken token)
    {
        queryCancellation = token;
        if (!reviewed.TryGetValue(action.Id, out var original) || original != action || action.Eligibility is not Eligibility.Eligible and not Eligibility.ReviewRequired)
        {
            return new(action.Id, "Skipped", 0, "Analyze again before running this manager action.");
        }

        if (token.IsCancellationRequested)
        {
            return new(action.Id, "Canceled", 0, "Canceled before starting.");
        }
        if (action.LocationQuery is not null)
        {
            try
            {
                if (!string.Equals(await Query(action.Tool, action.LocationQuery.ToArray()), action.Location, StringComparison.OrdinalIgnoreCase))
                    return new(action.Id, "Skipped", 0, "Cache location changed since analysis. Analyze again before cleaning.");
            }
            catch (IOException)
            {
                return new(action.Id, "Skipped", 0, "The reviewed cache location could not be rechecked. Analyze again before cleaning.");
            }
        }

        if (action.AvdName is not null)
        {
            if (emulatorRunning())
            {
                return new(action.Id, "Skipped", 0, "An emulator started after analysis.");
            }

            if (!(await Query(action.Tool, EmulatorCommand, "list")).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(action.AvdName, StringComparer.Ordinal))
            {
                return new(action.Id, "Skipped", 0, "Virtual-device inventory changed.");
            }

            try
            {
                if (action.AvdIdentity is not { } expected ||
                    !string.Equals(action.Location, Path.Combine(avdRoot(), action.AvdName + ".avd"), StringComparison.OrdinalIgnoreCase))
                    return new(action.Id, "Skipped", 0, "Virtual-device location changed. Analyze again before removing it.");
                var current = SnapshotAvd(avdRoot(), action.AvdName);
                if (current.Volume != expected.Volume || current.Id != expected.Id || current.Created != expected.Created)
                    return new(action.Id, "Skipped", 0, "Virtual device was replaced since analysis. Analyze again before removing it.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return new(action.Id, "Skipped", 0, "Virtual-device identity could not be rechecked.");
            }

        }
        if (token.IsCancellationRequested)
        {
            return new(action.Id, "Canceled", 0, "Canceled before starting.");
        }

        var r = await context.Runner.RunAsync(new(action.Tool.Executable, action.Tool.PrefixArguments.Concat(action.Arguments).ToArray(), context.WorkingDirectory, Processes.ExecutionKind.Mutation, Environment: action.Tool.Environment), null, CancellationToken.None);
        if (!r.Success)
        {
            return new(action.Id, "Failed", 0, "Manager action failed or its result was incomplete.");
        }

        queryCancellation = CancellationToken.None;
        if (action.AvdName is not null && (await Query(action.Tool, EmulatorCommand, "list")).Split('\n', StringSplitOptions.TrimEntries).Contains(action.AvdName, StringComparer.Ordinal))
        {
            return new(action.Id, "Failed", 0, "The virtual device remains registered.");
        }

        return new(action.Id, "Completed", 0, "Manager action completed. Reclaimed disk space was not measured.");
    }
    private async Task DiscoverNuGetAsync(List<ManagedCleanupAction> result)
    {
        var dotnet = context.Tools.Find("dotnet");
        if (dotnet is not null)
        {
            foreach (var cache in new[] { "http-cache", "temp", "plugins-cache", "global-packages" })
            {
                try
                {
                    var description = await Query(dotnet, "nuget", "locals", cache, "--list");
                    result.Add(new("nuget-" + cache, "Clear NuGet " + cache, CleanupCategory.PackageCaches, dotnet, ["nuget", "locals", cache, "--clear"], description, "Clears this NuGet cache through dotnet. Restores may download packages again.", Eligibility.ReviewRequired, LocationQuery: ["nuget", "locals", cache, "--list"]));
                }
                catch (IOException) { result.Add(new("nuget-" + cache, "NuGet " + cache, CleanupCategory.PackageCaches, dotnet, [], "Unknown", "Cache check unavailable.", Eligibility.Unavailable)); }
            }
        }
    }

    private async Task DiscoverNpmAsync(List<ManagedCleanupAction> result)
    {
        var npm = context.Tools.Find("npm");
        if (npm is not null)
        {
            try
            {
                result.Add(new("npm-cache", "Verify and reclaim npm cache", CleanupCategory.PackageCaches, npm, [CacheCommand, "verify"], await Query(npm, "config", "get", CacheCommand), "npm verifies integrity and garbage-collects unneeded cached data. It does not remove running npx environments.", Eligibility.ReviewRequired, LocationQuery: ["config", "get", CacheCommand]));
            }
            catch (IOException)
            { // An unavailable optional npm cache is not offered for removal.
            }
        }
    }

    private async Task DiscoverEmulatorsAsync(List<ManagedCleanupAction> result)
    {
        var android = context.Tools.Find("android");
        if (android is not null)
        {
            var root = avdRoot();
            try
            {
                foreach (var name in (await Query(android, EmulatorCommand, "list")).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!ProviderParser.ValidPackageId(name))
                    {
                        continue;
                    }

                    bool active = emulatorRunning();
                    var location = Path.Combine(root, name + ".avd");
                    FileIdentity? identity = null;
                    try
                    {
                        if (!active)
                            identity = SnapshotAvd(root, name);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { // An unavailable or redirected directory cannot identify the reviewed device.
                    }
                    result.Add(new("avd-" + name, name, CleanupCategory.Emulators, android, [EmulatorCommand, "remove", name], location,
                        active ? "An emulator is running; removal is blocked because ownership cannot be proved." : identity is null
                            ? "Virtual-device configuration or directory identity unavailable. Removal is blocked."
                            : "Stopped. Size and image identity unavailable. Removes this virtual device and its data. Shared system images are retained.",
                        active ? Eligibility.InUse : identity is null ? Eligibility.Unavailable : Eligibility.ReviewRequired, name, AvdIdentity: identity));
                }
            }
            catch (IOException) { result.Add(new("avd-error", "Emulators", CleanupCategory.Emulators, android, [], root, "Virtual-device state unavailable. Removal unavailable.", Eligibility.Unavailable)); }
        }
    }

    private static FileIdentity SnapshotAvd(string root, string name)
    {
        var location = Path.Combine(root, name + ".avd");
        if (!PathGuard.ValidLocalRoot(location))
            throw new IOException("Invalid virtual-device directory.");

        var configuration = Path.Combine(root, name + ".ini");
        PathGuard.Snapshot(configuration);
        var paths = File.ReadLines(configuration)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0] == "path")
            .Select(parts => parts[1]).ToArray();
        if (paths.Length != 1 || !PathGuard.ValidLocalRoot(paths[0]) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[0])),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(location))
            throw new IOException("The configured virtual-device directory does not match the reviewed directory.");

        return PathGuard.Snapshot(location);
    }

    private async Task DiscoverPipAsync(List<ManagedCleanupAction> result)
    {
        var pythonManager = context.Tools.Find("py");
        if (pythonManager is not null)
        {
            try
            {
                using var inventory = JsonDocument.Parse(await Query(pythonManager, "list", "--format=json"));
                foreach (var version in inventory.RootElement.GetProperty("versions").EnumerateArray())
                {
                    await DiscoverInterpreterCacheAsync(version, result);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { result.Add(new("pip-unavailable", "pip caches", CleanupCategory.PackageCaches, pythonManager, [], "Unknown", "Interpreter inventory unavailable.", Eligibility.Unavailable)); }
        }
    }
    private async Task DiscoverInterpreterCacheAsync(JsonElement version, List<ManagedCleanupAction> result)
    {
        var path = version.GetProperty("executable").GetString()!;
        if (!InstallationOwnership.IsAvailableInterpreter(path))
        {
            return;
        }

        var python = new ToolEntry(path, [], path);
        try
        {
            var location = await Query(python, "-m", "pip", CacheCommand, "dir");
            if (!PathGuard.ValidLocalRoot(location) || result.Any(a => a.Id.StartsWith("pip:", StringComparison.Ordinal) && string.Equals(a.Location, location, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            result.Add(new("pip:" + path, "Clear pip cache", CleanupCategory.PackageCaches, python, ["-m", "pip", CacheCommand, "purge"], location, "Clears this interpreter's configured wheel and HTTP caches. Does not remove environments. Size unknown; future installs may download again.", Eligibility.ReviewRequired, LocationQuery: ["-m", "pip", CacheCommand, "dir"]));
        }
        catch (IOException) { result.Add(new("pip:" + path, "pip cache", CleanupCategory.PackageCaches, python, [], path, "Cache location unavailable.", Eligibility.Unavailable)); }
    }
}
