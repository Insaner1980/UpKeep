using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class WslProviderTests
{
    private sealed class Runner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task ApprovalIsSingleUseAndMutationIsScopedToReviewedPackage(bool snap, int exitCode)
    {
        using var scratch = new Scratch();
        scratch.Write("wsl.exe", "");
        var runner = new Runner(r =>
        {
            var args = r.Arguments;
            if (r.Kind == ExecutionKind.Mutation)
                return new(exitCode, "", "");
            var output = args.Contains("/usr/bin/apt") ? (snap ? "Listing..." : "Listing...\nfixture/stable 2 amd64 [upgradable from: 1]")
                : args.Contains("--list") ? (snap ? "Name Version Rev Tracking Publisher\nfixture 2 42 stable fixture" : "All snaps up to date")
                : args.Contains("--simulate") ? "Inst fixture (2 fixture)"
                : snap ? "Name Version Rev Tracking Publisher\nfixture 2 42 stable fixture" : "2";
            return new(0, output, "");
        });
        var provider = new WslDistroProvider("FixtureOnly");
        var item = Assert.Single((await provider.CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken)).Items);
        var changed = item.Request() with
        {
            TargetVersion = "3"
        };
        Assert.Equal(UpdateState.Blocked, (await provider.UpdateAsync(changed, new Progress<OperationProgress>(), TestContext.Current.CancellationToken)).State);
        provider.ApprovePreview(item, await provider.PreviewAsync(item, TestContext.Current.CancellationToken));
        var result = await provider.UpdateAsync(item.Request(), new Progress<OperationProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(exitCode == 0 ? UpdateState.CompletedUnverified : UpdateState.NeedsInteraction, result.State);
        var mutation = Assert.Single(runner.Requests, r => r.Kind == ExecutionKind.Mutation);
        Assert.Contains("FixtureOnly", mutation.Arguments);
        Assert.Contains(snap ? "--revision=42" : "fixture=2", mutation.Arguments);
        Assert.DoesNotContain("upgrade", mutation.Arguments);
        Assert.True((await provider.VerifyAsync(item.Request(), TestContext.Current.CancellationToken)).Verified);
        Assert.Equal(UpdateState.Blocked, (await provider.UpdateAsync(item.Request(), new Progress<OperationProgress>(), TestContext.Current.CancellationToken)).State);
        Assert.Single(runner.Requests, r => r.Kind == ExecutionKind.Mutation);
    }

    [Theory]
    [InlineData(0, "not apt", "not snap")]
    [InlineData(0, "Listing...", "Name Version Rev Tracking Publisher\nbroken")]
    [InlineData(1, "", "")]
    public async Task UnreadableInventoriesDisableAffectedManagers(int exitCode, string apt, string snap)
    {
        using var scratch = new Scratch();
        scratch.Write("wsl.exe", "");
        var runner = new Runner(r => new(exitCode, r.Arguments.Contains("/usr/bin/apt") ? apt : snap, ""));
        var result = await new WslDistroProvider("FixtureOnly").CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.All(result.Items, item => Assert.Equal(UpdateState.CheckUnavailable, item.State));
    }

    [Fact]
    public async Task FailedPreviewCannotApproveAndFailedVerificationDoesNotClaimSuccess()
    {
        using var scratch = new Scratch();
        scratch.Write("wsl.exe", "");
        var runner = new Runner(r => r.Arguments.Contains("/usr/bin/apt")
            ? new(0, "fixture/stable 2 amd64 [upgradable from: 1]", "") : new(1, "", "failed"));
        var provider = new WslDistroProvider("FixtureOnly");
        var item = (await provider.CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken)).Items[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PreviewAsync(item, TestContext.Current.CancellationToken));
        Assert.False((await provider.VerifyAsync(item.Request(), TestContext.Current.CancellationToken)).Verified);
        Assert.DoesNotContain(runner.Requests, r => r.Kind == ExecutionKind.Mutation);
    }
}
