using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;

namespace UpKeep.Core.Updates;

public sealed record DiscoveryContext(ToolLocator Tools, IProcessRunner Runner, string WorkingDirectory, IReadOnlySet<string>? SelectedEnvironments = null);
public interface IUpdateProvider
{
    string Id
    {
        get;
    }
    Task<ProviderCheckResult> CheckAsync(DiscoveryContext context, CancellationToken cancellationToken);
    Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
    Task<VerificationResult> VerifyAsync(UpdateRequest request, CancellationToken cancellationToken);
}
