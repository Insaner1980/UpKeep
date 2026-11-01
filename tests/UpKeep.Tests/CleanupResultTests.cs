using UpKeep.Core.Models;

namespace UpKeep.Tests;

public class CleanupResultTests
{
    [Fact]
    public void LaterFailuresRemainReachableWithTheirTarget()
    {
        var items = Enumerable.Range(0, 250).Select(i => new CleanupItemResult(i.ToString(), i == 201 ? "Failed" : "Skipped", 0, @"C:\fixture\file-" + i + ": result")).ToArray();
        var result = new CleanupResult(items);
        var page = result.Page(200);
        Assert.Equal(50, page.Count);
        Assert.Contains(page, i => i.State == "Failed" && i.Detail.Contains("file-201", StringComparison.Ordinal));
        Assert.Equal(200, result.Page(0).Count);
    }
}
