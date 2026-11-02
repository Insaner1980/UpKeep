using UpKeep.Core.Storage;

namespace UpKeep.Tests;

public class StorageLifecycleTests
{
    [Fact]
    public void OversizedSettingsSavePreservesTheReadablePreviousFile()
    {
        using var scratch = new Scratch();
        var store = new SettingsStore(new(scratch.Root));
        store.Save(new(1, [scratch.Root]));
        var roots = Enumerable.Range(0, 100).Select(i => Path.Combine(scratch.Root, i.ToString(),
            string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('a', 100), 8)))).ToArray();
        Assert.Throws<InvalidDataException>(() => store.Save(new(1, roots)));
        Assert.Null(store.Load().Warning);
        Assert.Equal(new[] { scratch.Root }, store.Load().Settings.ProjectRoots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrOversizedJournalIsReportedAsInterruptedAndPreserved(bool oversized)
    {
        using var scratch = new Scratch();
        var contents = oversized ? new string('x', 65537) : "{broken";
        var path = scratch.Write("last-operation.json", contents);
        var result = new OperationJournal(new(scratch.Root)).Load();
        Assert.Equal("Interrupted", result?.State);
        Assert.Equal("Unknown", result?.Kind);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void JournalLimitsSummaryWithoutLosingProgress()
    {
        using var scratch = new Scratch();
        var journal = new OperationJournal(new(scratch.Root));
        journal.Write(new("Cleanup", "Completed", 8, 7, new string('x', 5000)));
        var result = journal.Load();
        Assert.Equal(4096, result?.Summary.Length);
        Assert.Equal(7, result?.Completed);
        Assert.Equal("Completed", result?.State);
    }

    [Fact]
    public void SettingsRejectDriveRootsAndOversizedDocuments()
    {
        using var scratch = new Scratch();
        var store = new SettingsStore(new(scratch.Root));
        Assert.Throws<InvalidDataException>(() => store.Save(new(1, [Path.GetPathRoot(scratch.Root)!])));
        var contents = new string(' ', 65537);
        var path = scratch.Write("settings.json", contents);
        Assert.NotNull(store.Load().Warning);
        Assert.Equal(contents, File.ReadAllText(path));
    }
    [Fact]
    public void RapidJournalWritesRemainReadableAndAtomic()
    {
        using var scratch = new Scratch();
        var journal = new OperationJournal(new(scratch.Root));
        for (int index = 0; index < 500; index++)
        {
            journal.Write(new("Cleanup", "Running", 500, index, "Progress"));
        }

        Assert.Equal(499, journal.Load()?.Completed);
        Assert.Single(Directory.GetFiles(scratch.Root));
    }
    [Fact]
    public void NullProjectRootIsMalformedAndPreserved()
    {
        using var scratch = new Scratch();
        scratch.Write("settings.json", "{\"SchemaVersion\":1,\"ProjectRoots\":[null]}");
        Assert.NotNull(new SettingsStore(new(scratch.Root)).Load().Warning);
    }
    [Fact]
    public void AtomicSettingsRoundTripPreservesOnlySelectedRoots()
    {
        using var scratch = new Scratch();
        var store = new SettingsStore(new(scratch.Root));
        store.Save(new(1, [scratch.Root]));
        Assert.Equal(new[] { scratch.Root }, store.Load().Settings.ProjectRoots);
        Assert.Single(Directory.GetFiles(scratch.Root));
    }
    [Fact]
    public void MalformedSettingsAreNotOverwritten()
    {
        using var scratch = new Scratch();
        var path = scratch.Write("settings.json", "{broken");
        var store = new SettingsStore(new(scratch.Root));
        Assert.NotNull(store.Load().Warning);
        Assert.Throws<InvalidDataException>(() => store.Save(new(1, [])));
        Assert.Equal("{broken", File.ReadAllText(path));
    }
    [Fact]
    public void FullDiskFailurePreservesPreviousSettings()
    {
        using var scratch = new Scratch();
        var paths = new AppPaths(scratch.Root);
        new SettingsStore(paths).Save(new(1, [scratch.Root]));
        var failing = new SettingsStore(paths, (_, _) => throw new IOException("Disk full"));
        Assert.Throws<IOException>(() => failing.Save(new(1, [])));
        Assert.Equal(new[] { scratch.Root }, new SettingsStore(paths).Load().Settings.ProjectRoots);
    }
    [Fact]
    public void PendingJournalBecomesInterruptedWithoutRetry()
    {
        using var scratch = new Scratch();
        var journal = new OperationJournal(new(scratch.Root));
        journal.Write(new("Updates", "Running", 4, 1, "One item completed"));
        var recovered = journal.Load();
        Assert.Equal("Interrupted", recovered?.State);
        Assert.Equal(1, recovered?.Completed);
    }
}
