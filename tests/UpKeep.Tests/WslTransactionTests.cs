using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class WslTransactionTests
{
    private sealed class Runner : IProcessRunner
    {
        public string Preview = "Inst sample (2 Ubuntu)"; public int Mutations; public int Calls;
        public Task<ProcessResult> RunAsync(ProcessRequest r, IProgress<ProcessOutput>? output, CancellationToken token)
        {
            Calls++;
            if (r.Kind == ExecutionKind.Mutation)
            {
                Mutations++;
            }

            return Task.FromResult(new ProcessResult(0, r.Arguments.Contains("--simulate") ? Preview : r.Arguments.Contains("/usr/bin/apt") ? "Listing...\nsample/stable 2 amd64 [upgradable from: 1]" : "All snaps up to date.", ""));
        }
    }
    [Fact]
    public async Task GlobalCheckNeverActivatesPreviouslySelectedDistribution()
    {
        using var scratch = new Scratch();
        scratch.Write("wsl.exe", "");
        var runner = new Runner();
        var coordinator = new UpdateCoordinator([new WslDistroProvider("FixtureOnly")], new(new ToolLocator([scratch.Root]), runner, scratch.Root), new(new(scratch.Root)), new());
        await coordinator.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, runner.Calls);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UnapprovedOrChangedTransactionCannotMutate(bool approve, bool change)
    {
        using var scratch = new Scratch();
        var runner = new Runner();
        var provider = new WslDistroProvider("FixtureOnly");
        var item = Assert.Single((await provider.CheckAsync(new(new ToolLocator(), runner, scratch.Root), TestContext.Current.CancellationToken)).Items);
        if (approve)
        {
            provider.ApprovePreview(item, await provider.PreviewAsync(item, TestContext.Current.CancellationToken));
        }

        if (change)
        {
            runner.Preview += "\nInst dependency";
        }

        var result = await provider.UpdateAsync(item.Request(), new Progress<OperationProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Blocked, result.State);
        Assert.Equal(0, runner.Mutations);
    }
}
