using UpKeep.Core.Cleanup;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UpKeep.Tests;

public class CleanupAnalyzerTests
{
    [Theory]
    [InlineData("src", "Debug")]
    [InlineData("tests", "Release")]
    public async Task GeneratedOutputUnderSourceTreesRemainsEligibleWithNestedProtections(string parent, string configuration)
    {
        using var scratch = new Scratch();
        scratch.Write("tools/git.exe", "");
        scratch.Write("project/.git/HEAD", "");
        var project = $"project/{parent}/App";
        scratch.Write(project + "/App.csproj", "");
        scratch.Write(project + $"/bin/{configuration}/app.dll", "");
        scratch.Write(project + $"/bin/{configuration}/tests/retained.dll", "");
        scratch.Write(project + $"/bin/{configuration}/auth.json", "");
        scratch.Write("project/secrets/App/App.csproj", "");
        scratch.Write("project/secrets/App/bin/Debug/private.dll", "");
        var context = new DiscoveryContext(new ToolLocator([Path.Combine(scratch.Root, "tools")]), new GitRunner(), scratch.Root);
        using var result = await new CleanupAnalyzer(new(Path.Combine(scratch.Root, "state")), context).AnalyzeAsync(
            new([new("project", Path.Combine(scratch.Root, "project"), RootKind.Project, CleanupCategory.TestArtifacts)]),
            new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
        var candidate = Assert.Single(result.Manifest.Read());
        Assert.EndsWith("app.dll", candidate.RelativePath);
        Assert.Equal(Eligibility.Eligible, candidate.Eligibility);
    }

    [Fact]
    public async Task InaccessibleSubdirectoryIsReportedWithoutLosingReadableSiblings()
    {
        using var scratch = new Scratch();
        var readable = scratch.Write("temp/readable/old.tmp", "data");
        scratch.Write("temp/blocked/old.tmp", "data");
        File.SetLastWriteTimeUtc(readable, DateTime.UtcNow.AddDays(-10));
        var blocked = new DirectoryInfo(Path.Combine(scratch.Root, "temp", "blocked"));
        var restricted = blocked.GetAccessControl();
        var denial = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        restricted.AddAccessRule(denial);
        try
        {
            blocked.SetAccessControl(restricted);
            Assert.Throws<UnauthorizedAccessException>(() => blocked.GetFileSystemInfos());
            using var result = await new CleanupAnalyzer(new(Path.Combine(scratch.Root, "state")),
                new(new ToolLocator(), new GitRunner(), scratch.Root)).AnalyzeAsync(new([new("temp", Path.Combine(scratch.Root, "temp"), RootKind.Temporary, CleanupCategory.TemporaryFiles)]), new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
            Assert.Contains(result.Manifest.Read(), c => c.RelativePath == Path.Combine("readable", "old.tmp"));
            Assert.Contains(result.Groups, g => g.Name == blocked.FullName && g.Eligibility == Eligibility.Unavailable);
            Assert.Contains(result.Warnings, w => w.Contains(blocked.FullName, StringComparison.Ordinal));
        }
        finally
        {
            var restored = blocked.GetAccessControl();
            restored.RemoveAccessRuleSpecific(denial);
            blocked.SetAccessControl(restored);
        }
    }
    private sealed class GitRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken token) => Task.FromResult(new ProcessResult(0, "artifacts/run-a/tracked.txt\0", ""));
    }
    [Fact]
    public async Task ProjectArtifactSetsAreSeparateAndProtectedFilesAreExcluded()
    {
        using var scratch = new Scratch();
        scratch.Write("project/app.csproj", "");
        scratch.Write("project/.git/HEAD", "");
        scratch.Write("project/artifacts/run-a/report.html", "");
        scratch.Write("project/artifacts/run-a/tracked.txt", "");
        scratch.Write("project/artifacts/run-b/report.html", "");
        scratch.Write("project/artifacts/run-a/auth.json", "");
        scratch.Write("project/artifacts/run-a/tests/example.cs", "");
        scratch.Write("tools/git.exe", "");
        var context = new DiscoveryContext(new ToolLocator([Path.Combine(scratch.Root, "tools")]), new GitRunner(), scratch.Root);
        using var result = await new CleanupAnalyzer(new(Path.Combine(scratch.Root, "state")), context).AnalyzeAsync(new([new("project", Path.Combine(scratch.Root, "project"), RootKind.Project, CleanupCategory.TestArtifacts)]), new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(2, result.Manifest.Read().Count());
        Assert.All(result.Groups, g => Assert.Equal(Eligibility.ReviewRequired, g.Eligibility));
    }
    [Fact]
    public async Task CancelLeavesNoIncompleteManifest()
    {
        using var scratch = new Scratch();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var paths = new UpKeep.Core.Storage.AppPaths(Path.Combine(scratch.Root, "state"));
        var analyzer = new CleanupAnalyzer(paths, new(new ToolLocator(), new GitRunner(), scratch.Root));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(new([]), new Progress<CleanupProgress>(), canceled.Token));
        Assert.False(Directory.Exists(paths.Temp) && Directory.EnumerateFileSystemEntries(paths.Temp).Any());
    }
    [Fact]
    public async Task WebBinSourceIsNotDotNetGeneratedOutput()
    {
        using var scratch = new Scratch();
        scratch.Write("project/package.json", "");
        scratch.Write("project/.git/HEAD", "");
        scratch.Write("project/bin/cli.js", "");
        using var result = await new CleanupAnalyzer(new(Path.Combine(scratch.Root, "state")), new(new ToolLocator(), new GitRunner(), scratch.Root)).AnalyzeAsync(new([new("project", Path.Combine(scratch.Root, "project"), RootKind.Project, CleanupCategory.TestArtifacts)]), new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Manifest.Read(), c => c.RelativePath.EndsWith("cli.js", StringComparison.Ordinal) && c.Eligibility == Eligibility.Eligible);
    }
}
