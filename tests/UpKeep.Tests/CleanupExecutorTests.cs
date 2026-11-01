using UpKeep.Core.Cleanup;
using UpKeep.Core.Models;
using UpKeep.Core.Storage;

namespace UpKeep.Tests;

public class CleanupExecutorTests
{
    private sealed class JournalBreakingRecycle(string journalPath) : IRecycleBinService
    {
        public Task<IReadOnlyList<CleanupItemResult>> RecycleAsync(IReadOnlyList<ValidatedCleanupFile> files, CancellationToken cancellationToken)
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
                Directory.CreateDirectory(journalPath);
            }
            return Task.FromResult<IReadOnlyList<CleanupItemResult>>(files.Select(file => new CleanupItemResult(file.Candidate.Id, "Recycled", file.Candidate.LogicalSize, "Fixture result")).ToArray());
        }
    }

    [Fact]
    public async Task JournalFailureBetweenCandidatesDoesNotDiscardEarlierResults()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("retained.tmp", "keep");
        var identity = PathGuard.Snapshot(file);
        using var manifest = new CleanupManifest(new(Path.Combine(scratch.Root, "manifest")));
        for (int i = 0; i < 3; i++)
            manifest.Append(new(i.ToString(), "root", "retained.tmp", CleanupCategory.TestArtifacts, "test", 4, identity, Eligibility.Eligible, RemovalMethod.Recycle, "", "group"));
        manifest.Seal();
        var analysis = new CleanupAnalysis(manifest, [new("root", scratch.Root, RootKind.Project, CleanupCategory.TestArtifacts, PathGuard.Snapshot(scratch.Root))], [], []);
        var journalRoot = Path.Combine(scratch.Root, "journal");
        var gate = new UpKeep.Core.Updates.OperationGate();
        var executor = new CleanupExecutor(new JournalBreakingRecycle(Path.Combine(journalRoot, "last-operation.json")), new(new(journalRoot)), gate);
        var result = await executor.ExecuteAsync(new(analysis, new HashSet<string>(), new HashSet<string> { "group" }), new Counter(), TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal("Recycled", result.Items[0].State);
        Assert.False(gate.IsBusy);
        Assert.Equal("keep", File.ReadAllText(file));
    }

    private sealed class FailingRecycle(string? journalPath = null) : IRecycleBinService
    {
        public async Task<IReadOnlyList<CleanupItemResult>> RecycleAsync(IReadOnlyList<ValidatedCleanupFile> files, CancellationToken cancellationToken)
        {
            if (journalPath is null)
            {
                throw new IOException("Fixture recycling failure");
            }

            File.Delete(journalPath);
            Directory.CreateDirectory(journalPath);
            // Cross the executor's progress interval to exercise a failed checkpoint write.
            await Task.Delay(250, cancellationToken);
            return files.Select(f => new CleanupItemResult(f.Candidate.Id, "Failed", 0, "Fixture recycling failure")).ToArray();
        }
    }

    [Fact]
    public async Task FailedProgressJournalPreservesFileAndReturnsCompletedAttempt()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("retained.tmp", "keep");
        using var analysis = Analyze(scratch, file, RemovalMethod.Recycle);
        var journalRoot = Path.Combine(scratch.Root, "journal");
        var gate = new UpKeep.Core.Updates.OperationGate();
        var progress = new Counter();
        var executor = new CleanupExecutor(new FailingRecycle(Path.Combine(journalRoot, "last-operation.json")), new(new(journalRoot)), gate);
        var result = await executor.ExecuteAsync(new(analysis, new HashSet<string>(), new HashSet<string> { "group" }), progress, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Completed);
        Assert.True(progress.Reports >= 2);
        Assert.Equal("keep", File.ReadAllText(file));
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task RecyclingExceptionSkipsFileWithoutPermanentFallback()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("retained.tmp", "keep");
        using var analysis = Analyze(scratch, file, RemovalMethod.Recycle);
        var result = await new CleanupExecutor(new FailingRecycle(), new(new(Path.Combine(scratch.Root, "journal"))), new()).ExecuteAsync(new(analysis, new HashSet<string> { "file" }, new HashSet<string>()), new Counter(), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Skipped);
        Assert.Contains("Fixture recycling failure", Assert.Single(result.Items).Detail);
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public async Task ChangedFileIsSkippedAndBusyGateIsNotReleasedByAnotherOperation()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("retained.tmp", "keep");
        using var analysis = Analyze(scratch, file, RemovalMethod.Permanent);
        File.AppendAllText(file, " changed");
        var gate = new UpKeep.Core.Updates.OperationGate();
        var executor = new CleanupExecutor(new RefuseRecycle(), new(new(Path.Combine(scratch.Root, "journal"))), gate);
        var selection = new CleanupSelection(analysis, new HashSet<string> { "file" }, new HashSet<string>());
        Assert.True(gate.TryEnter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(selection, new Counter(), TestContext.Current.CancellationToken));
        Assert.True(gate.IsBusy);
        gate.Exit();
        var result = await executor.ExecuteAsync(selection, new Counter(), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("keep changed", File.ReadAllText(file));
        Assert.False(gate.IsBusy);
    }
    private sealed class Counter : IProgress<CleanupProgress>
    {
        public int Reports; public void Report(CleanupProgress value) => Reports++;
    }
    private sealed class RefuseRecycle : IRecycleBinService
    {
        public Task<IReadOnlyList<CleanupItemResult>> RecycleAsync(IReadOnlyList<ValidatedCleanupFile> files, CancellationToken t) => Task.FromResult<IReadOnlyList<CleanupItemResult>>(files.Select(f => new CleanupItemResult(f.Candidate.Id, "Skipped", 0, "Recycling unavailable")).ToArray());
    }
    private static CleanupAnalysis Analyze(Scratch scratch, string path, RemovalMethod method)
    {
        var paths = new AppPaths(Path.Combine(scratch.Root, "app-data"));
        var manifest = new CleanupManifest(paths);
        var id = PathGuard.Snapshot(path);
        manifest.Append(new("file", "root", Path.GetRelativePath(scratch.Root, path), CleanupCategory.TemporaryFiles, "temp", id.Length, id, Eligibility.Eligible, method, "", "group"));
        manifest.Seal();
        return new(manifest, [new("root", scratch.Root, RootKind.Temporary, CleanupCategory.TemporaryFiles, PathGuard.Snapshot(scratch.Root))], [], []);
    }
    [Fact]
    public async Task NewChildAfterAnalyze_IsPreserved()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("old.tmp", "old");
        using var analysis = Analyze(scratch, file, RemovalMethod.Permanent);
        var newFile = scratch.Write("new.tmp", "keep");
        var result = await new CleanupExecutor(new RefuseRecycle(), new(new(Path.Combine(scratch.Root, "journal"))), new()).ExecuteAsync(new(analysis, new HashSet<string> { "file" }, new HashSet<string>()), new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Completed);
        Assert.False(File.Exists(file));
        Assert.Equal("keep", File.ReadAllText(newFile));
    }
    [Fact]
    public async Task RecycleFailureNeverFallsBackToPermanentRemoval()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("old.tmp", "keep");
        using var analysis = Analyze(scratch, file, RemovalMethod.Recycle);
        var result = await new CleanupExecutor(new RefuseRecycle(), new(new(Path.Combine(scratch.Root, "journal"))), new()).ExecuteAsync(new(analysis, new HashSet<string> { "file" }, new HashSet<string>()), new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(file));
        Assert.Equal(0, result.RecycledBytes);
    }
    [Fact]
    public async Task CancellationDoesNotRemoveQueuedFiles()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("old.tmp", "keep");
        using var analysis = Analyze(scratch, file, RemovalMethod.Permanent);
        var result = await new CleanupExecutor(new RefuseRecycle(), new(new(Path.Combine(scratch.Root, "journal"))), new()).ExecuteAsync(new(analysis, new HashSet<string> { "file" }, new HashSet<string>()), new Progress<CleanupProgress>(), new CancellationToken(true));
        Assert.Equal("Canceled", Assert.Single(result.Items).State);
        Assert.True(File.Exists(file));
    }
    [Fact]
    public async Task LargeInventoryDoesNotFloodProgressAndRetainsEveryTarget()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("retained.tmp", "keep");
        var identity = PathGuard.Snapshot(file);
        using var manifest = new CleanupManifest(new(Path.Combine(scratch.Root, "manifest")));
        for (int i = 0; i < 300; i++)
        {
            manifest.Append(new(i.ToString(), "root", "retained.tmp", CleanupCategory.TemporaryFiles, "test", 4, identity, Eligibility.Eligible, RemovalMethod.None, "", "group"));
        }

        manifest.Seal();
        var analysis = new CleanupAnalysis(manifest, [new("root", scratch.Root, RootKind.Temporary, CleanupCategory.TemporaryFiles, PathGuard.Snapshot(scratch.Root))], [], []);
        var progress = new Counter();
        var result = await new CleanupExecutor(new RefuseRecycle(), new(new(Path.Combine(scratch.Root, "journal"))), new()).ExecuteAsync(new(analysis, new HashSet<string>(), new HashSet<string> { "group" }), progress, TestContext.Current.CancellationToken);
        Assert.Equal(300, result.Skipped);
        Assert.True(progress.Reports < 300);
        Assert.Contains("retained.tmp", result.Items[200].Detail);
    }
}
