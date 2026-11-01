using System.Text.RegularExpressions;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;

namespace UpKeep.Core.Updates;

public sealed partial class WslDistroProvider(string distro) : IUpdateProvider
{
    public string Id => "wsl-distro:" + distro;
    private DiscoveryContext? context;
    private ToolEntry? wsl;
    private Dictionary<InstallationKey, UpdateItem> reviewed = [];
    private readonly Dictionary<InstallationKey, (string? Current, string? Target, string? Revision, string Preview)> approved = [];
    private async Task<ProcessResult> Run(string[] command, CancellationToken cancellationToken, ExecutionKind kind = ExecutionKind.Probe)
        => await context!.Runner.RunAsync(new(wsl!.Executable, new[] { "--distribution", distro, "--exec", "/usr/bin/env", "LC_ALL=C" }.Concat(command).ToArray(), context.WorkingDirectory, kind), null, cancellationToken);
    public static IReadOnlyList<PackageVersion> ParseApt(string text)
    {
        var result = new List<PackageVersion>();
        foreach (var line in ProviderParser.Normalize(text).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line == "Listing...")
            {
                continue;
            }

            var m = AptPackageRegex().Match(line);
            if (!m.Success)
            {
                throw new InvalidDataException("Unrecognized APT result. Package indexes were not refreshed automatically.");
            }

            result.Add(new(m.Groups[1].Value + ":" + m.Groups[3].Value, m.Groups[4].Value, m.Groups[2].Value));
        }
        return result;
    }
    public async Task<ProviderCheckResult> CheckAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        this.context = context;
        wsl = context.Tools.Find("wsl") ?? throw new InvalidOperationException("WSL is unavailable.");
        var rows = new List<UpdateItem>();
        var apt = await Run(["/usr/bin/apt", "list", "--upgradable"], cancellationToken);
        if (apt.Success)
        {
            try
            {
                foreach (var p in ParseApt(apt.StandardOutput))
                {
                    rows.Add(new(new(Id, "WSL:" + distro, p.Id, "/var/lib/dpkg/" + p.Id), p.Id, p.Current, p.Latest, UpdateState.UpdateAvailable, "APT / " + distro + ". Uses existing package indexes; freshness is not known. Review the transaction before installation.", Metadata: new(ManagerKind.Apt, wsl, distro)));
                }
            }
            catch (InvalidDataException e) { rows.Add(Error("APT", e.Message)); }
        }
        else
        {
            rows.Add(Error("APT", "APT could not be checked. This distribution may require configuration or authentication."));
        }

        var snap = await Run(["/usr/bin/snap", "refresh", "--list"], cancellationToken);
        AddSnapRows(snap, rows, wsl);
        reviewed = rows.ToDictionary(r => r.Key);
        return new(rows, !rows.Any(r => r.State == UpdateState.CheckUnavailable));
    }
    private UpdateItem Error(string name, string detail) => new(new(Id, "WSL:" + distro, name, name), name, null, null, UpdateState.CheckUnavailable, detail);
    public async Task<string> PreviewAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (item.Metadata?.Manager == ManagerKind.Snap)
        {
            return item.Detail + "\nRefresh this snap only; its configured channel is preserved.";
        }

        var result = await Run(["/usr/bin/apt-get", "--simulate", "install", "--only-upgrade", "--no-remove", item.Key.PackageId + "=" + item.TargetVersion], cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException("APT could not preview this transaction.");
        }

        return result.StandardOutput;
    }
    public void ApprovePreview(UpdateItem item, string preview) => approved[item.Key] = (item.CurrentVersion, item.TargetVersion, item.Metadata?.Extra, preview);
    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!reviewed.TryGetValue(request.Key, out var item) || item.Request() != request)
        {
            return new(UpdateState.Blocked, "Distro package changed; check again.");
        }

        if (!approved.Remove(request.Key, out var consent) || consent.Current != item.CurrentVersion || consent.Target != item.TargetVersion || consent.Revision != item.Metadata?.Extra || consent.Preview != await PreviewAsync(item, cancellationToken))
        {
            return new(UpdateState.Blocked, "The distro transaction needs a fresh preview and confirmation.");
        }

        var arguments = request.Metadata?.Manager == ManagerKind.Snap ? new[] { "/usr/bin/sudo", "-n", "/usr/bin/snap", "refresh", request.Key.PackageId, "--revision=" + request.Metadata.Extra } :
            ["/usr/bin/sudo", "-n", "/usr/bin/apt-get", "install", "--only-upgrade", "--no-remove", "-y", request.Key.PackageId + "=" + request.TargetVersion];
        var result = await Run(arguments, cancellationToken, ExecutionKind.Mutation);
        return new(result.Success ? UpdateState.CompletedUnverified : UpdateState.NeedsInteraction, result.Success ? "Distro manager completed; checking the installed version." : "Distro update could not complete. Configure sudo/authentication in that distribution and check again.", result.ExitCode);
    }
    public async Task<VerificationResult> VerifyAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        var result = request.Metadata?.Manager == ManagerKind.Snap ? await Run(["/usr/bin/snap", "list", request.Key.PackageId], cancellationToken) : await Run(["/usr/bin/dpkg-query", "--show", "--showformat=${Version}", request.Key.PackageId], cancellationToken);
        if (!result.Success)
        {
            return new(false, null);
        }

        if (request.Metadata?.Manager == ManagerKind.Snap)
        {
            var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
            var cells = WhitespaceRegex().Split(line ?? "");
            return new(cells.Length > 2 && cells[2] == request.Metadata.Extra, cells.Length > 1 ? cells[1] : null);
        }
        var version = result.StandardOutput.Trim();
        return new(version == request.TargetVersion, version);
    }

    [GeneratedRegex(@"^([a-z0-9][a-z0-9+.-]*)/\S+ (\S+) ([a-z0-9][a-z0-9-]*) \[upgradable from: (\S+)\]$", RegexOptions.None, 1000)]
    private static partial Regex AptPackageRegex();
    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex WhitespaceRegex();
    private void AddSnapRows(ProcessResult snap, List<UpdateItem> rows, ToolEntry tool)
    {
        if (snap.Success)
        {
            var lines = ProviderParser.Normalize(snap.StandardOutput).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0 && lines[0].StartsWith("Name", StringComparison.Ordinal))
            {
                foreach (var line in lines.Skip(1))
                {
                    var columns = WhitespaceRegex().Split(line);
                    if (columns.Length < 5 || !ProviderParser.ValidPackageId(columns[0]))
                    {
                        rows.Add(Error("Snap", "Unrecognized Snap refresh result."));
                        break;
                    }
                    rows.Add(new(new(Id, "WSL:" + distro, columns[0], "snap:" + columns[0]), columns[0], null, columns[1], UpdateState.UpdateAvailable, "Snap / " + distro + " / revision " + columns[2], Metadata: new(ManagerKind.Snap, tool, distro, columns[2])));
                }
            }
            else if (!((snap.StandardOutput + snap.StandardError).Contains("All snaps up to date", StringComparison.Ordinal)))
            {
                rows.Add(Error("Snap", "Snap refresh metadata is unavailable."));
            }
        }
        else
        {
            rows.Add(Error("Snap", "Snap daemon or authentication is unavailable."));
        }
    }
}
