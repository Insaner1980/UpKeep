using System.Diagnostics;
using System.Text.Json;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Storage;

namespace UpKeep.Tests;

public class ManifestLifecycleTests
{
    [Fact]
    public void RecognizedOrphanIsRemovedButUnknownAndCurrentSessionsRemain()
    {
        using var scratch = new Scratch();
        var paths = new AppPaths(scratch.Root);
        using var active = new CleanupManifest(paths);
        var abandoned = Path.Combine(paths.Temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "owner.json"), JsonSerializer.Serialize(new
        {
            Application = "UpKeep",
            Version = 1,
            ProcessId = int.MaxValue,
            StartTicks = 1L
        }));
        File.WriteAllText(Path.Combine(abandoned, "cleanup-manifest.jsonl"), "");
        var unknown = Path.Combine(paths.Temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");
        CleanupManifest.RemoveOrphans(paths);
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(unknown));
        Assert.True(Directory.Exists(Path.Combine(paths.Temp, active.Id)));
    }
    [Fact]
    public void RecognizedSessionWithUnexpectedFileIsPreserved()
    {
        using var scratch = new Scratch();
        var paths = new AppPaths(scratch.Root);
        var session = Path.Combine(paths.Temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "owner.json"), JsonSerializer.Serialize(new
        {
            Application = "UpKeep",
            Version = 1,
            ProcessId = int.MaxValue,
            StartTicks = 1L
        }));
        File.WriteAllText(Path.Combine(session, "keep.txt"), "keep");
        CleanupManifest.RemoveOrphans(paths);
        Assert.True(File.Exists(Path.Combine(session, "keep.txt")));
    }
    [Fact]
    public void DiscardingTwiceIsSafe()
    {
        using var scratch = new Scratch();
        var manifest = new CleanupManifest(new(scratch.Root));
        manifest.Seal();
        manifest.Dispose();
        manifest.Dispose();
        Assert.False(Directory.Exists(Path.Combine(new AppPaths(scratch.Root).Temp, manifest.Id)));
    }
}
