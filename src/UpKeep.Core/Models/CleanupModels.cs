namespace UpKeep.Core.Models;

public enum CleanupCategory
{
    TemporaryFiles, TestArtifacts, OldLogs, PackageCaches, Emulators
}
public enum Eligibility
{
    Eligible, ReviewRequired, InUse, Protected, Unavailable
}
public enum RemovalMethod
{
    Recycle, Permanent, CacheCommand, AvdCommand, None
}
public enum RootKind
{
    Temporary, Logs, Project, ProtectedSystem, ReviewOnly
}
public sealed record FileIdentity(uint Volume, ulong Id, long Length, long LastWrite, long Created, uint Links);
public sealed record CleanupRoot(string Id, string Path, RootKind Kind, CleanupCategory Category, FileIdentity? Identity = null);
public sealed record CleanupCandidate(string Id, string RootId, string RelativePath, CleanupCategory Category, string RuleId, long LogicalSize, FileIdentity Identity, Eligibility Eligibility, RemovalMethod Method, string Reason, string Group);
public sealed record CleanupGroup(string Id, string Name, CleanupCategory Category, long LogicalSize, int Count, Eligibility Eligibility, string Reason);
public sealed record CleanupScope(IReadOnlyList<CleanupRoot> Roots);
public sealed record CleanupProgress(int Completed, int Skipped, int Failed, string Message);
public sealed record CleanupItemResult(string Id, string State, long LogicalSize, string Detail);
public sealed record CleanupResult(IReadOnlyList<CleanupItemResult> Items)
{
    public IReadOnlyList<CleanupItemResult> Page(int offset) => Items.Skip(Math.Max(0, offset)).Take(200).ToArray();
    public int Completed => Items.Count(i => i.State is "Removed" or "Recycled" or "Completed");
    public int Skipped => Items.Count(i => i.State.StartsWith("Skipped", StringComparison.Ordinal));
    public int Failed => Items.Count(i => i.State == "Failed");
    public long RecycledBytes => Items.Where(i => i.State == "Recycled").Sum(i => i.LogicalSize);
}
