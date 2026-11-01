using UpKeep.Core.Models;
using UpKeep_App;

namespace UpKeep.Tests;

public class CleanupTextTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(12, "12 B")]
    [InlineData(1200, "1.2 KB")]
    [InlineData(1_200_000, "1.2 MB")]
    [InlineData(1_200_000_000, "1.2 GB")]
    public void SizePreservesSmallNonzeroMeasurements(long bytes, string expected) => Assert.Equal(expected, CleanupText.Size(bytes));

    [Fact]
    public void UnmeasuredLocationsDoNotClaimZeroFilesOrBytes()
    {
        var group = new CleanupGroup("cache", "Cache", CleanupCategory.PackageCaches, 0, 0, Eligibility.Protected, "Managed by its owner.");
        var text = CleanupText.CategorySummary(CleanupCategory.PackageCaches, [group], 9, true);
        Assert.Contains("not measured", text);
        Assert.Contains("9 cleanup options in Details", text);
        Assert.DoesNotContain("0 files", text);
        Assert.DoesNotContain("0.0 MB", text);
    }

    [Fact]
    public void ManagedDevicesWithoutFileInventoryHaveUnmeasuredSize()
    {
        var text = CleanupText.CategorySummary(CleanupCategory.Emulators, [], 2, true);
        Assert.Contains("not measured", text);
        Assert.Contains("2 cleanup options in Details", text);
        Assert.DoesNotContain("0 files", text);
    }

    [Fact]
    public void UnconfiguredProjectsAreNotReportedAsAnEmptyScan()
    {
        var text = CleanupText.CategorySummary(CleanupCategory.TestArtifacts, [], 0, false);
        Assert.Contains("Not scanned", text);
        Assert.Contains("Settings", text);
        Assert.DoesNotContain("0", text);
    }

    [Fact]
    public void PartialScanLabelsOnlyTheKnownCandidates()
    {
        CleanupGroup[] groups = [new("known", "Logs", CleanupCategory.OldLogs, 12, 1, Eligibility.Eligible, "Old log"),
            new("missing", "Other logs", CleanupCategory.OldLogs, 0, 0, Eligibility.Unavailable, "Unavailable")];
        var text = CleanupText.CategorySummary(CleanupCategory.OldLogs, groups, 0, true);
        Assert.Contains("1 cleanup candidate / 12 B", text);
        Assert.Contains("partial", text);
    }

    [Fact]
    public void EmptyCompletedScanReportsNoCandidates() =>
        Assert.Equal("No cleanup candidates found.", CleanupText.CategorySummary(CleanupCategory.TestArtifacts, [], 0, true));

    [Fact]
    public void SelectionDistinguishesNoSelectionFromUnmeasuredActions()
    {
        Assert.Equal("No items selected.", CleanupText.SelectionSummary(0, false, 0));
        var actions = CleanupText.SelectionSummary(0, false, 2);
        Assert.Contains("not measured", actions);
        Assert.DoesNotContain("0 B", actions);
        Assert.Contains("12 B", CleanupText.SelectionSummary(12, true, 1));
        Assert.Contains("not measured", CleanupText.SelectionSummary(12, true, 1));
    }
}
