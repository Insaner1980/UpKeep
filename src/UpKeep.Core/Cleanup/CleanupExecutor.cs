using UpKeep.Core.Models;
using UpKeep.Core.Storage;
using UpKeep.Core.Updates;

namespace UpKeep.Core.Cleanup;

public interface IRecycleBinService
{
    Task<IReadOnlyList<CleanupItemResult>> RecycleAsync(IReadOnlyList<ValidatedCleanupFile> files, CancellationToken cancellationToken);
}
public sealed class CleanupExecutor(IRecycleBinService recycle, OperationJournal journal, OperationGate gate)
{
    private const string OperationName = "Cleanup";
    private const string SkippedState = "Skipped";
    public Task<CleanupResult> ExecuteAsync(CleanupSelection selection, IProgress<CleanupProgress> progress, CancellationToken token) => Task.Run(() => ExecuteCoreAsync(selection, progress, token), CancellationToken.None);

    private async Task<CleanupResult> ExecuteCoreAsync(CleanupSelection selection, IProgress<CleanupProgress> progress, CancellationToken token)
    {
        if (!gate.TryEnter())
        {
            throw new InvalidOperationException("Another operation is running.");
        }

        var results = new List<CleanupItemResult>();
        bool journalFailed = false;
        int completed = 0, skipped = 0, failed = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var roots = selection.Analysis.Roots.ToDictionary(r => r.Id);
        try
        {
            journal.Write(new(OperationName, "Pending", selection.CandidateIds.Count, 0, "Reviewed cleanup selection is pending."));
            foreach (var candidate in selection.Analysis.Manifest.Read())
            {
                if (!IsSelected(selection, candidate))
                {
                    continue;
                }

                var root = roots[candidate.RootId];
                var target = Path.Combine(root.Path, candidate.RelativePath);
                var resultStart = results.Count;
                if (token.IsCancellationRequested || journalFailed)
                {
                    results.Add(new(candidate.Id, "Canceled", 0, target + ": Canceled before removal."));
                    continue;
                }
                journal.Write(new(OperationName, "Running", 0, results.Count, "Removing reviewed items."));
                await RemoveCandidateAsync(root, candidate, results, token);
                AddResultDetails(results, resultStart, target, ref completed, ref skipped, ref failed);
                if (clock.ElapsedMilliseconds >= 200)
                {
                    journalFailed = !SaveProgress(results.Count);
                    progress.Report(new(completed, skipped, failed, "Cleaning reviewed items…"));
                    clock.Restart();
                }
            }
            try
            {
                journal.Write(new(OperationName, "Completed", results.Count, results.Count, "Cleanup finished. Recycled data is not freed disk space."));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { // Completed removals remain completed; the in-memory result is still returned.
            }
            progress.Report(new(completed, skipped, failed, "Cleanup finished."));
            return new CleanupResult(results);
        }
        finally { gate.Exit(); }
    }
    private static bool IsSelected(CleanupSelection selection, CleanupCandidate candidate) =>
        (selection.CandidateIds.Contains(candidate.Id) || selection.EligibleGroups.Contains(candidate.Group)) &&
        candidate.Eligibility is Eligibility.Eligible or Eligibility.ReviewRequired;

    private bool SaveProgress(int count)
    {
        try
        {
            journal.Write(new(OperationName, "Running", 0, count, "Completed items remain completed; review individual results."));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RemoveCandidateAsync(CleanupRoot root, CleanupCandidate candidate, List<CleanupItemResult> results, CancellationToken token)
    {
        var validation = PathGuard.Validate(root, candidate);
        if (!validation.Valid)
        {
            results.Add(new(candidate.Id, SkippedState, 0, validation.Reason));
        }
        else
        {
            using var opened = validation.File!;
            try
            {
                if (candidate.Method == RemovalMethod.Permanent)
                {
                    PathGuard.Delete(opened);
                    results.Add(new(candidate.Id, "Removed", candidate.LogicalSize, "Removed. Logical size is not a measurement of reclaimed disk space."));
                }
                else if (candidate.Method == RemovalMethod.Recycle)
                {
                    results.AddRange(await recycle.RecycleAsync([opened], token));
                }
                else
                {
                    results.Add(new(candidate.Id, SkippedState, 0, "Unsupported removal method."));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                results.Add(new(candidate.Id, SkippedState, 0, error.Message));
            }
        }
    }
    private static void AddResultDetails(List<CleanupItemResult> results, int resultStart, string target, ref int completed, ref int skipped, ref int failed)
    {
        for (int index = resultStart; index < results.Count; index++)
        {
            var item = results[index];
            results[index] = item with
            {
                Detail = target + ": " + item.Detail
            };
            if (item.State is "Removed" or "Recycled" or "Completed")
            {
                completed++;
            }
            else if (item.State.StartsWith(SkippedState, StringComparison.Ordinal))
            {
                skipped++;
            }
            else if (item.State == "Failed")
            {
                failed++;
            }
        }
    }
}
