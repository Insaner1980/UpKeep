using Microsoft.Management.Deployment;
using UpKeep.Core.Models;

namespace UpKeep.Core.Updates;

public sealed class WinGetProvider : IUpdateProvider
{
    public string Id => "winget";
    private readonly Dictionary<InstallationKey, CatalogPackage> packages = [];
    private PackageManager? manager;
    private DiscoveryContext? context;
    public Task<ProviderCheckResult> CheckAsync(DiscoveryContext context, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        this.context = context;
        packages.Clear();
        var rows = new List<UpdateItem>();
        try
        {
            manager ??= new PackageManager();
            var accepted = await ConnectSourcesAsync(rows);
            foreach (var scope in new[] { PackageInstallScope.User, PackageInstallScope.System })
            {
                await AddScopeAsync(scope, accepted, rows, cancellationToken);
            }
            return new ProviderCheckResult(rows, !rows.Any(r => r.Key.InstallIdentity.StartsWith("source:", StringComparison.Ordinal)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ProviderCheckResult(rows.Append(new UpdateItem(new(Id, "Windows", "catalog", "winget:catalog"), "WinGet", null, null, UpdateState.CheckUnavailable, "WinGet catalog unavailable: " + e.Message)).ToArray(), false, e.ToString());
        }
    }, cancellationToken);
    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!packages.TryGetValue(request.Key, out var package) || manager is null || package.InstalledVersion?.Version != request.CurrentVersion || package.DefaultInstallVersion?.Version != request.TargetVersion)
        {
            return new(UpdateState.Blocked, "Installation changed; check again.");
        }

        PackageVersionId? version = null;
        var versions = package.AvailableVersions;
        for (int i = 0; i < versions.Count; i++)
        {
            if (versions[i].Version == request.TargetVersion)
            {
                version = versions[i];
                break;
            }
        }

        if (version is null)
        {
            return new(UpdateState.Blocked, "The reviewed version is no longer available.");
        }

        var options = new InstallOptions();
        options.PackageVersionId = version;
        options.AcceptPackageAgreements = false;
        options.PackageInstallScope = request.Key.EnvironmentId.EndsWith("System", StringComparison.Ordinal) ? PackageInstallScope.System : PackageInstallScope.User;
        var operation = manager.UpgradePackageAsync(package, options);
        operation.Progress = (_, p) => progress.Report(new(0, 0, package.Name, UpdateState.Running, p.State.ToString()));
        var result = await operation;
        return new(result.Status switch
        {
            InstallResultStatus.Ok when result.RebootRequired => UpdateState.RestartRequired,
            InstallResultStatus.Ok => UpdateState.CompletedUnverified,
            InstallResultStatus.PackageAgreementsNotAccepted => UpdateState.NeedsInteraction,
            _ => UpdateState.Failed
        }, result.Status.ToString(), unchecked((int)result.InstallerErrorCode));
    }
    public async Task<VerificationResult> VerifyAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return new(false, null);
        }

        var result = await CheckAsync(context, cancellationToken);
        var row = result.Items.FirstOrDefault(i => i.Key == request.Key);
        return new(row?.CurrentVersion == request.TargetVersion, row?.CurrentVersion);
    }
    private async Task<List<PackageCatalogReference>> ConnectSourcesAsync(List<UpdateItem> rows)
    {
        var accepted = new List<PackageCatalogReference>();
        var catalogs = manager!.GetPackageCatalogs();
        for (int c = 0; c < catalogs.Count; c++)
        {
            var source = catalogs[c];
            source.AcceptSourceAgreements = false;
            var available = await source.ConnectAsync();
            if (available.Status == ConnectResultStatus.Ok)
            {
                accepted.Add(source);
            }
            else
            {
                rows.Add(new(new(Id, "Windows", source.Info.Id, "source:" + source.Info.Id), "WinGet / " + source.Info.Name, null, null, UpdateState.CheckUnavailable, "Source requires attention: " + available.Status + ". Source agreements were not accepted automatically."));
            }
        }
        return accepted;
    }
    private async Task AddScopeAsync(PackageInstallScope scope, IReadOnlyList<PackageCatalogReference> accepted, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        var options = new CreateCompositePackageCatalogOptions();
        options.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;
        options.InstalledScope = scope;
        foreach (var source in accepted)
        {
            options.Catalogs.Add(source);
        }

        var connection = await manager!.CreateCompositePackageCatalog(options).ConnectAsync();
        if (connection.Status != ConnectResultStatus.Ok)
        {
            throw new InvalidOperationException("WinGet catalog connection: " + connection.Status);
        }

        var found = await connection.PackageCatalog.FindPackagesAsync(new FindPackagesOptions());
        if (found.Status != FindPackagesResultStatus.Ok)
        {
            throw new InvalidOperationException("WinGet catalog search: " + found.Status);
        }

        for (int index = 0; index < found.Matches.Count; index++)
        {
            var match = found.Matches[index];
            cancellationToken.ThrowIfCancellationRequested();
            var package = match.CatalogPackage;
            AddPackage(package, scope, rows);
        }
    }
    private void AddPackage(CatalogPackage package, PackageInstallScope scope, List<UpdateItem> rows)
    {
        var installed = package.InstalledVersion;
        if (installed is null)
        {
            return;
        }

        var target = package.DefaultInstallVersion;
        var sourceId = target?.PackageCatalog?.Info?.Id ?? "local";
        var location = installed.GetMetadata(PackageVersionMetadataField.InstalledLocation);
        var productCodes = installed.ProductCodes;
        var codes = new List<string>();
        for (int c = 0; c < productCodes.Count; c++)
        {
            codes.Add(productCodes[c]);
        }

        var identity = string.IsNullOrWhiteSpace(location) ? sourceId + "|" + package.Id + "|" + string.Join(",", codes) : location;
        var key = new InstallationKey(Id, "Windows:" + scope, package.Id, identity);
        var known = !string.IsNullOrWhiteSpace(installed.Version) && !installed.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        var state = package.IsUpdateAvailable ? UpdateState.UpdateAvailable : UpdateState.UpToDate;
        if (!known || target is null)
            state = UpdateState.CheckUnavailable;
        rows.Add(new(key, package.Name, installed.Version, target?.Version, state, "WinGet / " + scope + " / " + sourceId));
        packages[key] = package;
    }
}
