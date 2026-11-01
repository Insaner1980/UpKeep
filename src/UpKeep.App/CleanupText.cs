using System.Globalization;
using UpKeep.Core.Models;

namespace UpKeep_App;

internal static class CleanupText
{
    public static string Size(long bytes) => bytes switch
    {
        < 1_000 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1_000_000 => (bytes / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        < 1_000_000_000 => (bytes / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
    };

    public static string CategorySummary(CleanupCategory category, IReadOnlyList<CleanupGroup> groups, int actionCount, bool hasProjectFolders)
    {
        if (category == CleanupCategory.TestArtifacts && !hasProjectFolders)
        {
            return "Not scanned. Add project folders in Settings.";
        }

        var count = groups.Sum(g => g.Count);
        bool unmeasured = groups.Any(g => g.Count == 0);
        var options = $"{actionCount} cleanup option{(actionCount == 1 ? "" : "s")} in Details / size not measured";
        if (count == 0)
        {
            if (actionCount > 0)
            {
                return options;
            }

            return unmeasured ? "Size not measured. Open Details." : "No cleanup candidates found.";
        }
        return $"{count} cleanup candidate{(count == 1 ? "" : "s")} / {Size(groups.Sum(g => g.LogicalSize))}" +
            (unmeasured ? " / partial scan" : "") + (actionCount > 0 ? " / " + options : "");
    }

    public static string SelectionSummary(long bytes, bool hasFiles, int actionCount)
    {
        if (!hasFiles && actionCount == 0)
        {
            return "No items selected.";
        }

        var files = hasFiles ? $"Selected file size: {Size(bytes)}" : "";
        if (actionCount == 0)
        {
            return files;
        }

        return files + (hasFiles ? " + " : "") + $"{actionCount} cleanup option{(actionCount == 1 ? "" : "s")} selected / size not measured";
    }
}
