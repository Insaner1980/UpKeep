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
    [InlineData("WindowsTemp", true)]
    [InlineData("SystemTemp", false)]
    [InlineData("LocalServiceTemp", false)]
    [InlineData("NetworkServiceTemp", false)]
    public async Task SystemTempCannotBeScannedThroughAnOrdinaryRoot(string rootId, bool supported)
    {
        using var scratch = new Scratch();
        var path = Path.Combine(UpKeep.Core.Elevation.CleanupProtocol.ResolveRoot(rootId), "UpKeep-do-not-enumerate-" + Guid.NewGuid().ToString("N"));
        using var analysis = await new CleanupAnalyzer(new(scratch.Root), new(new ToolLocator([]), new GitRunner(), scratch.Root))
            .AnalyzeAsync(new([new("temp", path, RootKind.Temporary, CleanupCategory.TemporaryFiles)]),
                new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
        Assert.Empty(analysis.Manifest.Read());
        Assert.Empty(analysis.Roots);
        Assert.Contains("Administrator access required", Assert.Single(analysis.Groups).Reason);
        var reason = Assert.Single(analysis.Groups).Reason;
        Assert.Equal(supported, reason.Contains("Use Analyze system temporary files", StringComparison.Ordinal));
        if (!supported)
            Assert.Contains("Analysis is unavailable", reason);
    }

    [Theory]
    [InlineData("src")]
    [InlineData("tests")]
    public async Task ReportsUnderSourceTreesRemainReviewableWithoutExposingProtectedContent(string parent)
    {
        using var scratch = new Scratch();
        scratch.Write("tools/git.exe", "");
        scratch.Write("project/.git/HEAD", "");
        var project = $"project/{parent}/App";
        scratch.Write(project + "/App.csproj", "");
        var output = project + "/reports/run";
        scratch.Write(output + "/report.html", "");
        scratch.Write(output + "/tracked.html", "");
        scratch.Write(output + "/example.cs", "");
        scratch.Write(output + "/auth.json", "");
        scratch.Write(output + "/tests/retained.html", "");
        scratch.Write("project/secrets/App/App.csproj", "");
        scratch.Write("project/secrets/App/reports/run/private.html", "");
        var context = new DiscoveryContext(new ToolLocator([Path.Combine(scratch.Root, "tools")]),
            new GitRunner($"{parent}/App/reports/run/tracked.html\0"), scratch.Root);
        using var result = await new CleanupAnalyzer(new(Path.Combine(scratch.Root, "state")), context).AnalyzeAsync(
            new([new("project", Path.Combine(scratch.Root, "project"), RootKind.Project, CleanupCategory.TestArtifacts)]),
            new Progress<CleanupProgress>(), TestContext.Current.CancellationToken);
        var candidate = Assert.Single(result.Manifest.Read());
        Assert.EndsWith("report.html", candidate.RelativePath);
        Assert.Equal(Eligibility.ReviewRequired, candidate.Eligibility);
        Assert.Equal(RemovalMethod.Recycle, candidate.Method);
    }

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
    private sealed class GitRunner(string tracked = "artifacts/run-a/tracked.txt\0") : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken token) => Task.FromResult(new ProcessResult(0, tracked, ""));
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
