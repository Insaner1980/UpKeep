using UpKeep.Core.Discovery;

namespace UpKeep.Core.Models;

public readonly record struct InstallationKey(string ProviderId, string EnvironmentId, string PackageId, string InstallIdentity);
public enum UpdateState
{
    Checking, UpToDate, UpdateAvailable, CheckUnavailable, UnsupportedInstallation, Blocked, Queued, Running, NeedsInteraction, SucceededVerified, CompletedUnverified, Failed, Canceled, RestartRequired
}
public enum UpdateKind
{
    ExactPackage, RunUpdater, Group, CheckEnvironment
}
public enum ManagerKind
{
    Npm, Pnpm, Bun, Pip, Uv, Python, Claude, CodeRabbit, Sonar, AndroidCli, AndroidSdk, DotNetTool, VisualStudio, Wsl, Apt, Snap, CodeExtension, PowerShell, TruffleHog
}
public sealed record PackageMetadata(ManagerKind Manager, ToolEntry Tool, string Scope = "", string? Extra = null);
public sealed record UpdateItem(InstallationKey Key, string Name, string? CurrentVersion, string? TargetVersion, UpdateState State, string Detail,
    UpdateKind Kind = UpdateKind.ExactPackage, PackageMetadata? Metadata = null)
{
    public bool Eligible => State == UpdateState.UpdateAvailable && Kind != UpdateKind.RunUpdater;
    public bool CanRun => Eligible || (State == UpdateState.CheckUnavailable && Kind == UpdateKind.RunUpdater && Metadata is not null);
    public UpdateRequest Request() => new(Key, CurrentVersion, TargetVersion, Kind, Metadata);
}
public sealed record UpdateRequest(InstallationKey Key, string? CurrentVersion, string? TargetVersion, UpdateKind Kind, PackageMetadata? Metadata);
public sealed record ProviderCheckResult(IReadOnlyList<UpdateItem> Items, bool Complete, string Diagnostics = "");
public sealed record UpdateResult(UpdateState State, string Detail, int? ExitCode = null);
public sealed record VerificationResult(bool Verified, string? InstalledVersion, string Detail = "");
public sealed record OperationProgress(int Current, int Total, string Name, UpdateState State, string Detail = "");
