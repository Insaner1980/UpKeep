using System.Text.Json;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class ManagedActionsTests
{
    [Theory]
    [InlineData("pnpm")]
    [InlineData("uv")]
    [InlineData("bun")]
    [InlineData("npm")]
    [InlineData("dotnet")]
    [InlineData("py")]
    public async Task ChangedCacheLocationCannotRunTheReviewedAction(string tool)
    {
        using var scratch = new Scratch();
        scratch.Write(tool + ".exe", "");
        var python = scratch.Write("python.exe", "");
        var location = scratch.Root;
        var runner = new Runner(r => new(0, r.Arguments.Contains("--format=json")
            ? JsonSerializer.Serialize(new
            {
                versions = new[] { new { executable = python } }
            }) : location, ""));
        var service = new ManagedActions(new(new ToolLocator([scratch.Root]), runner, scratch.Root), () => false);
        var actions = await service.DiscoverAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(actions);
        location = Path.Combine(scratch.Root, "different-cache");
        foreach (var action in actions)
            Assert.Equal("Skipped", (await service.ExecuteAsync(action, TestContext.Current.CancellationToken)).State);
        Assert.DoesNotContain(runner.Requests, r => r.Kind == ExecutionKind.Mutation);
    }

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

    [Fact]
    public async Task ReviewedCachesRunOnlyTheirManagerAndArguments()
    {
        using var scratch = new Scratch();
        foreach (var tool in new[] { "pnpm", "uv", "bun", "dotnet", "npm", "py" })
            scratch.Write(tool + ".exe", "");
        var python = scratch.Write("python.exe", "");
        var runner = new Runner(r => new(0, r.Arguments.Contains("--format=json")
            ? JsonSerializer.Serialize(new
            {
                versions = new[] { new { executable = python }, new { executable = python } }
            }) : scratch.Root, ""));
        var service = new ManagedActions(new(new ToolLocator([scratch.Root]), runner, scratch.Root), () => false);
        var actions = await service.DiscoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(9, actions.Count);
        Assert.All(actions, a => Assert.Equal(Eligibility.ReviewRequired, a.Eligibility));
        foreach (var action in actions)
        {
            var result = await service.ExecuteAsync(action, TestContext.Current.CancellationToken);
            Assert.Equal("Completed", result.State);
            var mutation = runner.Requests.Last();
            Assert.Equal(ExecutionKind.Mutation, mutation.Kind);
            Assert.Equal(action.Tool.Executable, mutation.Executable);
            Assert.Equal(action.Arguments, mutation.Arguments);
            Assert.Equal(scratch.Root, mutation.WorkingDirectory);
        }
        Assert.Equal("Skipped", (await service.ExecuteAsync(actions[0] with
        {
            Arguments = ["unexpected"]
        }, TestContext.Current.CancellationToken)).State);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        Assert.Equal("Canceled", (await service.ExecuteAsync(actions[0], canceled.Token)).State);
    }

    [Fact]
    public async Task FailedQueriesNeverEnableRemoval()
    {
        using var scratch = new Scratch();
        foreach (var tool in new[] { "pnpm", "uv", "bun", "dotnet", "npm", "py", "android" })
            scratch.Write(tool + ".exe", "");
        var runner = new Runner(_ => new(1, "", "fixture failure"));
        var service = new ManagedActions(new(new ToolLocator([scratch.Root]), runner, scratch.Root), () => false);
        var actions = await service.DiscoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(9, actions.Count);
        foreach (var action in actions)
        {
            Assert.Equal(Eligibility.Unavailable, action.Eligibility);
            Assert.Equal("Skipped", (await service.ExecuteAsync(action, TestContext.Current.CancellationToken)).State);
        }
        Assert.DoesNotContain(runner.Requests, r => r.Kind == ExecutionKind.Mutation);
    }

    [Theory]
    [InlineData("success", "Completed")]
    [InlineData("running", "Skipped")]
    [InlineData("changed", "Skipped")]
    [InlineData("remains", "Failed")]
    [InlineData("failure", "Failed")]
    [InlineData("replaced", "Skipped")]
    [InlineData("root-changed", "Skipped")]
    [InlineData("stop-after-current", "Completed")]
    public async Task EmulatorRemovalRechecksOwnershipAndResult(string scenario, string state)
    {
        using var scratch = new Scratch();
        scratch.Write("android.exe", "");
        var avdRoot = scratch.Root;
        Directory.CreateDirectory(Path.Combine(avdRoot, "FixtureDevice.avd"));
        using var stop = new CancellationTokenSource();
        bool analyzed = false, removed = false;
        var runner = new Runner(r =>
        {
            if (r.Kind == ExecutionKind.Mutation)
            {
                removed = true;
                if (scenario == "stop-after-current")
                    stop.Cancel();
                return new(scenario == "failure" ? 1 : 0, "", "");
            }
            return new(0, (analyzed && scenario == "changed") || (removed && scenario != "remains") ? "" : "FixtureDevice\n../invalid", "");
        });
        var service = new ManagedActions(new(new ToolLocator([scratch.Root]), runner, scratch.Root), () => analyzed && scenario == "running", () => avdRoot);
        var action = Assert.Single(await service.DiscoverAsync(TestContext.Current.CancellationToken));
        analyzed = true;
        if (scenario == "replaced")
        {
            Directory.Move(Path.Combine(avdRoot, "FixtureDevice.avd"), Path.Combine(avdRoot, "previous-device"));
            Directory.CreateDirectory(Path.Combine(avdRoot, "FixtureDevice.avd"));
        }
        if (scenario == "root-changed")
            avdRoot = Path.Combine(scratch.Root, "changed-root");
        Assert.Equal(state, (await service.ExecuteAsync(action, stop.Token)).State);
        Assert.Equal(state != "Skipped", removed);
    }

    [Fact]
    public async Task InvalidCacheLocationsAndInterpreterInventoryAreUnavailable()
    {
        using var scratch = new Scratch();
        scratch.Write("pnpm.exe", "");
        scratch.Write("py.exe", "");
        var runner = new Runner(_ => new(0, "unrecognized", ""));
        var actions = await new ManagedActions(new(new ToolLocator([scratch.Root]), runner, scratch.Root)).DiscoverAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(Eligibility.Unavailable, a.Eligibility));
    }
}
