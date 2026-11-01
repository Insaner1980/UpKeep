using UpKeep.Core.Discovery;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class WinGetIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveReadOnly")]
    public async Task UnpackagedWinGetCatalogCanBeQueriedWithoutMutation()
    {
        using var scratch = new Scratch();
        var result = await new WinGetProvider().CheckAsync(new(new ToolLocator(), new ProcessRunner(), scratch.Root), TestContext.Current.CancellationToken);
        Assert.True(result.Items.Any(i => !string.IsNullOrWhiteSpace(i.CurrentVersion)), result.Diagnostics);
        Assert.All(result.Items.Where(i => i.Key.InstallIdentity.StartsWith("source:", StringComparison.Ordinal)), i => Assert.Equal(UpKeep.Core.Models.UpdateState.CheckUnavailable, i.State));
    }
}
