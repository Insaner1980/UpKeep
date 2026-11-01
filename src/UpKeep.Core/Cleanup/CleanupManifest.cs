using System.Diagnostics;
using System.Text.Json;
using UpKeep.Core.Models;
using UpKeep.Core.Storage;

namespace UpKeep.Core.Cleanup;

public sealed class CleanupManifest : IDisposable
{
    private const string OwnerFileName = "owner.json";
    private readonly string session;
    private readonly string path;
    private StreamWriter? writer;
    private FileStream? lease;
    private bool disposed;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public CleanupManifest(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Temp);
        session = Path.Combine(paths.Temp, Id);
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, OwnerFileName), JsonSerializer.Serialize(new
        {
            Application = "UpKeep",
            Version = 1,
            ProcessId = Environment.ProcessId,
            StartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks
        }));
        path = Path.Combine(session, "cleanup-manifest.jsonl");
        writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
    }
    public void Append(CleanupCandidate candidate) => (writer ?? throw new InvalidOperationException("Manifest is sealed.")).WriteLine(JsonSerializer.Serialize(candidate));
    public void Seal()
    {
        writer?.Dispose();
        writer = null;
        lease = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    public IEnumerable<CleanupCandidate> Read()
    {
        if (lease is null)
        {
            throw new InvalidOperationException("Analysis is incomplete or discarded.");
        }

        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return JsonSerializer.Deserialize<CleanupCandidate>(line) ?? throw new InvalidDataException("Invalid manifest record.");
        }
    }
    public IReadOnlyList<CleanupCandidate> Page(string group, int offset, int limit = 200) => Read().Where(c => c.Group == group).Skip(offset).Take(Math.Min(limit, 200)).ToArray();
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writer?.Dispose();
        writer = null;
        lease?.Dispose();
        lease = null;
        File.Delete(path);
        File.Delete(Path.Combine(session, OwnerFileName));
        if (!Directory.EnumerateFileSystemEntries(session).Any())
        {
            Directory.Delete(session);
        }
    }
    public static void RemoveOrphans(AppPaths paths)
    {
        if (!Directory.Exists(paths.Temp))
        {
            return;
        }

        foreach (var directory in new DirectoryInfo(paths.Temp).EnumerateDirectories())
        {
            RemoveOrphan(directory);
        }
    }
    private static void RemoveOrphan(DirectoryInfo directory)
    {
        try
        {
            if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            var files = directory.EnumerateFileSystemInfos().Take(3).ToArray();
            if (files.Length == 0 || files.Any(f => f is not FileInfo || f.Name is not OwnerFileName and not "cleanup-manifest.jsonl" || f.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                return;
            }

            var owner = files.OfType<FileInfo>().SingleOrDefault(f => f.Name == OwnerFileName);
            if (owner is null || owner.Length > 1024)
            {
                return;
            }

            using var json = JsonDocument.Parse(File.ReadAllText(owner.FullName));
            var root = json.RootElement;
            if (root.GetProperty("Application").GetString() != "UpKeep" || root.GetProperty("Version").GetInt32() != 1)
            {
                return;
            }

            if (OwnerIsAlive(root))
            {
                return;
            }

            var cleanupRoot = new CleanupRoot(directory.Name, directory.FullName, RootKind.Temporary, CleanupCategory.TemporaryFiles, PathGuard.Snapshot(directory.FullName));
            RemoveOwnedFiles(cleanupRoot, files);
            if (!directory.EnumerateFileSystemInfos().Any())
            {
                directory.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException or InvalidOperationException)
        { // Leave an unreadable or changed session untouched; ownership could not be revalidated.
        }
    }

    private static bool OwnerIsAlive(JsonElement root)
    {
        var pid = root.GetProperty("ProcessId").GetInt32();
        var ticks = root.GetProperty("StartTicks").GetInt64();
        bool alive;
        try
        {
            using var process = Process.GetProcessById(pid);
            alive = process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { alive = false; }
        return alive;
    }

    private static void RemoveOwnedFiles(CleanupRoot cleanupRoot, FileSystemInfo[] files)
    {
        foreach (var file in files)
        {
            var identity = PathGuard.Snapshot(file.FullName);
            var candidate = new CleanupCandidate(file.Name, cleanupRoot.Id, file.Name, cleanupRoot.Category, "OwnedScratch", identity.Length, identity, Eligibility.Eligible, RemovalMethod.Permanent, "Abandoned UpKeep scratch file", cleanupRoot.Id);
            var validated = PathGuard.Validate(cleanupRoot, candidate);
            if (validated.Valid)
            {
                using var handle = validated.File!;
                PathGuard.Delete(handle);
            }
        }
    }

}
public sealed record CleanupAnalysis(CleanupManifest Manifest, IReadOnlyList<CleanupRoot> Roots, IReadOnlyList<CleanupGroup> Groups, IReadOnlyList<string> Warnings) : IDisposable
{
    public void Dispose() => Manifest.Dispose();
}
public sealed record CleanupSelection(CleanupAnalysis Analysis, IReadOnlySet<string> CandidateIds, IReadOnlySet<string> EligibleGroups);
