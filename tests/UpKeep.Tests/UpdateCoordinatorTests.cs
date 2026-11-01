using UpKeep.Core.Models;
using UpKeep.Core.Storage;
using UpKeep.Core.Updates;
using UpKeep.Core.Discovery;
using UpKeep.Core.Processes;

namespace UpKeep.Tests;

public class UpdateCoordinatorTests
{
    [Fact]
    public async Task BusyGateRejectsChecksAndUpdatesWithoutStartingProviders()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var gate = new OperationGate();
        Assert.True(gate.TryEnter());
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator([]), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CheckAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync([provider.Item("one")], new ProgressLog(), TestContext.Current.CancellationToken));
        Assert.Equal(0, provider.Started);
        Assert.True(gate.IsBusy);
        gate.Exit();
    }

    [Fact]
    public async Task FailedCatalogIsUnavailableAndCanceledRevalidationDoesNotMutate()
    {
        using var scratch = new Scratch();
        var provider = new Provider { OnCheck = () => throw new IOException("fixture catalog failure") };
        var gate = new OperationGate();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator([]), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), gate);
        Assert.Equal(UpdateState.CheckUnavailable, Assert.Single(await coordinator.CheckAsync(TestContext.Current.CancellationToken)).State);
        provider.OnCheck = () => throw new OperationCanceledException();
        var results = await coordinator.ExecuteAsync([provider.Item("one")], new ProgressLog(), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Canceled, Assert.Single(results).Result.State);
        Assert.Equal(0, provider.Started);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task JournalFailureAfterCompletedUpdateStopsRemainingWork()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var gate = new OperationGate();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator([]), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), gate);
        provider.OnVerify = () =>
        {
            var path = Path.Combine(scratch.Root, "last-operation.json");
            File.Delete(path);
            Directory.CreateDirectory(path);
        };
        var results = await coordinator.ExecuteAsync([provider.Item("one"), provider.Item("two")], new ProgressLog(), TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Started);
        Assert.Equal(UpdateState.SucceededVerified, results[0].Result.State);
        Assert.Contains("could not be saved", results[0].Result.Detail);
        Assert.Equal(UpdateState.Canceled, results[1].Result.State);
        Assert.True(coordinator.StopRequested);
        Assert.False(gate.IsBusy);
    }
    private sealed class Provider : IUpdateProvider
    {
        public string Id => "fake"; public int Started; public Action? OnRun; public Action? OnCheck; public Action? OnVerify; public bool Verify = true;
        public UpdateItem Item(string id) => new(new(Id, "Windows", id, id), id, "1", "2", UpdateState.UpdateAvailable, "");
        public Task<ProviderCheckResult> CheckAsync(DiscoveryContext c, CancellationToken t)
        {
            OnCheck?.Invoke();
            return Task.FromResult(new ProviderCheckResult([Item("one"), Item("two")], true));
        }
        public async Task<UpdateResult> UpdateAsync(UpdateRequest r, IProgress<OperationProgress> p, CancellationToken t)
        {
            Started++;
            OnRun?.Invoke();
            await Task.Delay(10, CancellationToken.None);
            return new(UpdateState.CompletedUnverified, "");
        }
        public Task<VerificationResult> VerifyAsync(UpdateRequest r, CancellationToken t)
        {
            OnVerify?.Invoke();
            return Task.FromResult(new VerificationResult(Verify, Verify ? "2" : null));
        }
    }
    private sealed class ProgressLog : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }
    [Fact]
    public async Task ProgressIsReportedBeforeRevalidationAndVerification()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var progress = new ProgressLog();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator(), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), new());
        OperationProgress? beforeCheck = null, beforeInstall = null, beforeVerify = null;
        provider.OnCheck = () => beforeCheck = progress.Items.LastOrDefault();
        provider.OnRun = () => beforeInstall = progress.Items.LastOrDefault();
        provider.OnVerify = () => beforeVerify = progress.Items.LastOrDefault();
        await coordinator.ExecuteAsync([provider.Item("one")], progress, TestContext.Current.CancellationToken);
        Assert.NotNull(beforeCheck);
        Assert.Equal(UpdateState.Checking, beforeCheck.State);
        Assert.NotNull(beforeInstall);
        Assert.Equal(UpdateState.Running, beforeInstall.State);
        Assert.NotNull(beforeVerify);
        Assert.Equal(UpdateState.Checking, beforeVerify.State);
        Assert.NotEqual(beforeCheck.Detail, beforeVerify.Detail);
        Assert.All(progress.Items, p => { Assert.Equal(1, p.Current); Assert.Equal(1, p.Total); Assert.Equal("one", p.Name); });
        Assert.Equal(UpdateState.SucceededVerified, progress.Items[^1].State);
    }
    [Fact]
    public async Task FailedRevalidationReportsFailureAfterChecking()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var progress = new ProgressLog();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator(), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), new());
        provider.OnCheck = () => throw new IOException("Catalog unavailable");
        await coordinator.ExecuteAsync([provider.Item("one")], progress, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { UpdateState.Checking, UpdateState.Failed }, progress.Items.Select(p => p.State));
        Assert.Equal(0, provider.Started);
    }
    [Fact]
    public async Task StopAfterCurrentSkipsRemainingItems()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator(), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), new());
        provider.OnRun = coordinator.StopAfterCurrent;
        var result = await coordinator.ExecuteAsync([provider.Item("one"), provider.Item("two")], new Progress<OperationProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Started);
        Assert.Equal(UpdateState.Canceled, result[1].Result.State);
        Assert.Equal(UpdateState.SucceededVerified, result[0].Result.State);
    }
    [Fact]
    public async Task InstallerExitWithoutVersionProofIsUnverified()
    {
        using var scratch = new Scratch();
        var provider = new Provider { Verify = false };
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator(), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), new());
        var result = await coordinator.ExecuteAsync([provider.Item("one")], new Progress<OperationProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.CompletedUnverified, Assert.Single(result).Result.State);
    }
    [Fact]
    public async Task ChangedTargetIsNotExecuted()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator(), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), new());
        var result = await coordinator.ExecuteAsync([provider.Item("one") with { TargetVersion = "3" }], new Progress<OperationProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Blocked, Assert.Single(result).Result.State);
        Assert.Equal(0, provider.Started);
    }
    [Fact]
    public async Task DuplicateInstallationIsRejected()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator(), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync([provider.Item("one"), provider.Item("one")], new Progress<OperationProgress>(), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task StopDuringRevalidationDoesNotStartInstaller()
    {
        using var scratch = new Scratch();
        var provider = new Provider();
        var coordinator = new UpdateCoordinator([provider], new(new ToolLocator(), new ProcessRunner(), scratch.Root), new(new(scratch.Root)), new());
        provider.OnCheck = coordinator.StopAfterCurrent;
        var result = await coordinator.ExecuteAsync([provider.Item("one")], new Progress<OperationProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(0, provider.Started);
        Assert.Equal(UpdateState.Canceled, Assert.Single(result).Result.State);
    }
}
