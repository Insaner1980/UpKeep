using UpKeep.Core.Cleanup;
using UpKeep.Core.Discovery;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class ManagedActionTests
{
    private sealed class Runner : IProcessRunner
    {
        public int Mutations;
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken token)
        {
            if (request.Kind == ExecutionKind.Mutation)
            {
                Mutations++;
            }

            return Task.FromResult(new ProcessResult(0, "FixtureAvd\nOtherAvd\n", ""));
        }
    }
    [Fact]
    public async Task EmulatorStartingAfterAnalyzeBlocksRemoval()
    {
        using var scratch = new Scratch();
        scratch.Write("android.exe", "");
        var runner = new Runner();
        bool active = false;
        var service = new ManagedActions(new(new ToolLocator([scratch.Root]), runner, scratch.Root), () => active);
        var item = (await service.DiscoverAsync(TestContext.Current.CancellationToken))[0];
        active = true;
        Assert.Equal("Skipped", (await service.ExecuteAsync(item, TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, runner.Mutations);
    }
    [Fact]
    public async Task ARunningEmulatorIsNeverSelectable()
    {
        using var scratch = new Scratch();
        scratch.Write("android.exe", "");
        var service = new ManagedActions(new(new ToolLocator([scratch.Root]), new Runner(), scratch.Root), () => true);
        Assert.All(await service.DiscoverAsync(TestContext.Current.CancellationToken), i => Assert.Equal(UpKeep.Core.Models.Eligibility.InUse, i.Eligibility));
    }
}
