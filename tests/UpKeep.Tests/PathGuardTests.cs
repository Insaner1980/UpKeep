using UpKeep.Core.Cleanup;
using UpKeep.Core.Models;

namespace UpKeep.Tests;

public class PathGuardTests
{
    private static (CleanupRoot, CleanupCandidate) Analyze(string root, string path)
    {
        var identity = PathGuard.Snapshot(path);
        return (new("root", root, RootKind.Temporary, CleanupCategory.TemporaryFiles, PathGuard.Snapshot(root)), new("item", "root", Path.GetRelativePath(root, path), CleanupCategory.TemporaryFiles, "temp", identity.Length, identity, Eligibility.Eligible, RemovalMethod.Permanent, "", "group"));
    }
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"\\?\C:\Temp")]
    [InlineData(@"C:\Temp:stream")]
    [InlineData(@"C:\Temp\..\outside")]
    public void UnsafeRootsAreRejected(string root) => Assert.False(PathGuard.ValidLocalRoot(root));
    [Fact]
    public void UnchangedFileCanBeRemovedThroughValidatedHandle()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("old.tmp", "temporary");
        var (root, candidate) = Analyze(scratch.Root, file);
        var result = PathGuard.Validate(root, candidate);
        Assert.True(result.Valid, result.Reason);
        using (var opened = result.File!)
        {
            PathGuard.Delete(opened);
        }
        Assert.False(File.Exists(file));
    }
    [Fact]
    public void FileReplacedAfterAnalyze_IsSkipped()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("old.tmp", "first");
        var (root, candidate) = Analyze(scratch.Root, file);
        File.Delete(file);
        File.WriteAllText(file, "other");
        Assert.False(PathGuard.Validate(root, candidate).Valid);
        Assert.Equal("other", File.ReadAllText(file));
    }
    [Fact]
    public void FileModifiedAfterAnalyze_IsSkipped()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("old.tmp", "first");
        var (root, candidate) = Analyze(scratch.Root, file);
        File.AppendAllText(file, "new");
        Assert.False(PathGuard.Validate(root, candidate).Valid);
    }
    [Fact]
    public void SiblingPrefixAndTraversalCannotEscapeRoot()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("inside/a.tmp", "first");
        var (root, candidate) = Analyze(Path.Combine(scratch.Root, "inside"), file);
        var outside = scratch.Write("outside/sentinel.txt", "preserve");
        Assert.False(PathGuard.Validate(root, candidate with
        {
            RelativePath = "../outside/sentinel.txt"
        }).Valid);
        Assert.Equal("preserve", File.ReadAllText(outside));
    }
    [Fact]
    public void AncestorBecomesJunctionAfterAnalyze_IsRejected()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("inside/a.tmp", "first");
        var (root, candidate) = Analyze(scratch.Root, file);
        Directory.Move(Path.Combine(scratch.Root, "inside"), Path.Combine(scratch.Root, "old"));
        var outside = Path.Combine(scratch.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = scratch.Write("outside/a.tmp", "preserve");
        Directory.CreateSymbolicLink(Path.Combine(scratch.Root, "inside"), outside);
        Assert.False(PathGuard.Validate(root, candidate).Valid);
        Assert.Equal("preserve", File.ReadAllText(sentinel));
    }
    [Fact]
    public void LockedFileIsSkipped()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("old.tmp", "first");
        var (root, candidate) = Analyze(scratch.Root, file);
        using var locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(PathGuard.Validate(root, candidate).Valid);
    }
}
