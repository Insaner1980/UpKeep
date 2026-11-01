using System.Text.Json;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class ProviderInventoryIntegrationTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    [Fact]
    [Trait("Category", "LiveReadOnly")]
    public async Task InstalledCommandProvidersExposeHonestReadOnlyResults()
    {
        using var scratch = new Scratch();
        var context = new DiscoveryContext(new ToolLocator(), new ProcessRunner(), scratch.Root);
        var report = new List<object>();
        foreach (var kind in Enum.GetValues<ManagerKind>().Where(k => k is not ManagerKind.Apt and not ManagerKind.Snap))
        {
            var result = await new CommandProvider(kind).CheckAsync(context, TestContext.Current.CancellationToken);
            report.Add(new
            {
                Provider = kind.ToString(),
                result.Complete,
                Items = result.Items.Select(i => new { i.Name, State = i.State.ToString(), i.Detail })
            });
            Assert.All(result.Items.Where(i => i.Kind == UpdateKind.RunUpdater), i => Assert.Equal(UpdateState.CheckUnavailable, i.State));
        }
        var output = Path.GetFullPath(Path.Combine(scratch.Root, "..", "..", "tests", "provider-coverage.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, ReportOptions));
    }
}
