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
        Directory.CreateDirectory(Path.Combine(scratch.Root, "FixtureAvd.avd"));
        Directory.CreateDirectory(Path.Combine(scratch.Root, "OtherAvd.avd"));
        scratch.Write("FixtureAvd.ini", "path=" + Path.Combine(scratch.Root, "FixtureAvd.avd"));
        scratch.Write("OtherAvd.ini", "path=" + Path.Combine(scratch.Root, "OtherAvd.avd"));
        var service = new ManagedActions(new(new ToolLocator([scratch.Root]), runner, scratch.Root), () => active, () => scratch.Root);
        var item = (await service.DiscoverAsync(TestContext.Current.CancellationToken))[0];
        Assert.Equal(UpKeep.Core.Models.Eligibility.ReviewRequired, item.Eligibility);
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
