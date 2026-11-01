using UpKeep.Core.Cleanup;
using UpKeep.Core.Elevation;
using UpKeep.Core.Models;

namespace UpKeep.CleanupHost;

internal static class ProtectedTempService
{
    public static IEnumerable<CleanupCandidate> Analyze(string rootId)
    {
        var path = CleanupProtocol.ResolveRoot(rootId);
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }
                if (entry.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7))
                {
                    continue;
                }

                var identity = PathGuard.Snapshot(entry.FullName);
                yield return new(Guid.NewGuid().ToString("N"), rootId, Path.GetRelativePath(path, entry.FullName), CleanupCategory.TemporaryFiles, "ProtectedTemp", identity.Length, identity, Eligibility.Eligible, RemovalMethod.Permanent, "Administrator access required; permanently removes this old temporary file.", rootId);
            }
        }
    }
    public static IReadOnlyList<CleanupItemResult> Remove(ProtectedTempRequest request)
    {
        var path = CleanupProtocol.ResolveRoot(request.RootId);
        var root = new CleanupRoot(request.RootId, path, RootKind.ProtectedSystem, CleanupCategory.TemporaryFiles, PathGuard.Snapshot(path));
        var results = new List<CleanupItemResult>();
        foreach (var candidate in request.Candidates)
        {
            if (DateTime.FromFileTimeUtc(candidate.Identity.LastWrite) > DateTime.UtcNow.AddDays(-7))
            {
                results.Add(new(candidate.Id, "Skipped", 0, "The file is too recent."));
                continue;
            }
            var check = PathGuard.Validate(root, candidate);
            if (!check.Valid)
            {
                results.Add(new(candidate.Id, "Skipped", 0, check.Reason));
                continue;
            }
            using var file = check.File!;
            try
            {
                PathGuard.Delete(file);
                results.Add(new(candidate.Id, "Removed", candidate.LogicalSize, "Temporary file removed. Reclaimed disk space is not measured."));
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { results.Add(new(candidate.Id, "Skipped", 0, e.Message)); }
        }
        return results;
    }
}
