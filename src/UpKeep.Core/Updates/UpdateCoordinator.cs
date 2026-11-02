using UpKeep.Core.Models;
using UpKeep.Core.Storage;

namespace UpKeep.Core.Updates;

public sealed class OperationGate
{
    private int busy;
    public bool IsBusy => busy != 0;
    public bool TryEnter() => Interlocked.CompareExchange(ref busy, 1, 0) == 0;
    public void Exit() => Interlocked.Exchange(ref busy, 0);
}
public sealed class UpdateCoordinator(IReadOnlyList<IUpdateProvider> providers, DiscoveryContext context, OperationJournal journal, OperationGate gate)
{
    private const string OperationName = "Updates";
    public bool StopRequested
    {
        get; private set;
    }
    public void StopAfterCurrent() => StopRequested = true;
    public async Task<IReadOnlyList<UpdateItem>> CheckAsync(CancellationToken token)
    {
        if (gate.IsBusy)
        {
            throw new InvalidOperationException("Wait for the current operation.");
        }

        var rows = new List<UpdateItem>();
        foreach (var provider in providers)
        {
            if (provider is WslDistroProvider)
            {
                continue;
            }

            token.ThrowIfCancellationRequested();
            try
            {
                var result = await provider.CheckAsync(context, token);
                rows.AddRange(result.Items);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                rows.Add(new(new(provider.Id, "Windows", provider.Id, provider.Id), provider.Id, null, null, UpdateState.CheckUnavailable, e.Message));
            }
        }
        return Discovery.InstallationOwnership.Resolve(rows).OrderByDescending(i => i.Eligible).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task<IReadOnlyList<(InstallationKey Key, UpdateResult Result)>> ExecuteAsync(IReadOnlyList<UpdateItem> selected, IProgress<OperationProgress> progress, CancellationToken token)
    {
        var plan = selected.ToArray();
        if (plan.Length == 0 || plan.DistinctBy(i => i.Key).Count() != plan.Length || plan.Any(i => !i.CanRun))
        {
            throw new InvalidOperationException("Select distinct eligible installations.");
        }

        if (!gate.TryEnter())
        {
            throw new InvalidOperationException("Another operation is running.");
        }

        var results = new List<(InstallationKey Key, UpdateResult Result)>();
        StopRequested = false;
        try
        {
            journal.Write(new(OperationName, "Pending", plan.Length, 0, "Selected updates are pending."));
            foreach (var item in plan)
            {
                if (StopRequested || token.IsCancellationRequested)
                {
                    results.Add((item.Key, new(UpdateState.Canceled, "Canceled before starting.")));
                    continue;
                }
                var result = await UpdateOneAsync(item, results.Count + 1, plan.Length, progress, token);
                results.Add((item.Key, result));
                progress.Report(new(results.Count, plan.Length, item.Name, result.State, result.Detail));
                try
                {
                    journal.Write(new(OperationName, "Running", plan.Length, results.Count, result.Detail));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StopRequested = true; results[^1] = (item.Key, result with { Detail = result.Detail + " The result summary could not be saved. Remaining items will not start." }); }
            }
            try
            {
                journal.Write(new(OperationName, "Completed", plan.Length, results.Count, "Update batch finished. Review individual results."));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { // Completed updates remain completed; the in-memory results are still returned.
            }
            return results;
        }
        finally { gate.Exit(); }
    }
    private async Task<UpdateResult> UpdateOneAsync(UpdateItem item, int current, int total, IProgress<OperationProgress> progress, CancellationToken token)
    {
        UpdateResult result;
        try
        {
            var provider = providers.Single(p => p.Id == item.Key.ProviderId);
            progress.Report(new(current, total, item.Name, UpdateState.Checking, "Checking installation before updating…"));
            var check = await provider.CheckAsync(context, token);
            var fresh = check.Items.SingleOrDefault(i => i.Key == item.Key);
            if (StopRequested || token.IsCancellationRequested)
            {
                result = new(UpdateState.Canceled, "Canceled before starting.");
            }
            else if (fresh is null || fresh.CurrentVersion != item.CurrentVersion || fresh.TargetVersion != item.TargetVersion || fresh.Kind != item.Kind || !fresh.CanRun)
            {
                result = new(UpdateState.Blocked, "Installation or target changed since the check. Check again and select it again.");
            }
            else
            {
                journal.Write(new(OperationName, "Running", total, current - 1, "Working on " + item.Name));
                progress.Report(new(current, total, item.Name, UpdateState.Running, "Starting update…"));
                result = await provider.UpdateAsync(fresh.Request(), progress, CancellationToken.None);
                if (result.State == UpdateState.CompletedUnverified)
                {
                    progress.Report(new(current, total, item.Name, UpdateState.Checking, "Verifying installed version…"));
                    try
                    {
                        var verified = await provider.VerifyAsync(fresh.Request(), CancellationToken.None);
                        result = result with
                        {
                            State = verified.Verified ? UpdateState.SucceededVerified : UpdateState.CompletedUnverified,
                            Detail = verified.Verified ? "Installed version verified: " + verified.InstalledVersion : "Update finished; version not verified"
                        };
                    }
                    catch (Exception error)
                    {
                        result = result with
                        {
                            Detail = "Update finished; version not verified. " + error.Message
                        };
                    }
                }
            }
        }
        catch (OperationCanceledException) { result = new(UpdateState.Canceled, "Canceled before starting."); }
        catch (Exception error) { result = new(UpdateState.Failed, error.Message); }
        return result;
    }
}
