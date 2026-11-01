using UpKeep.Core.Models;

namespace UpKeep.Core.Cleanup;

public static class CleanupPolicy
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".kt", ".kts", ".java", ".js", ".jsx", ".ts", ".tsx", ".py", ".ps1", ".sh", ".cmd", ".bat", ".md", ".json", ".yaml", ".yml", ".toml", ".ini", ".config", ".cfg", ".properties", ".gradle", ".csproj", ".sln", ".slnx", ".xml", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".sql" };
    private static readonly HashSet<string> ProtectedParts = new(StringComparer.OrdinalIgnoreCase) { ".git", "src", "tests", "test", "androidTest", "sessions", "memories", "memories_v2", "worktrees", "attachments", "generated_images", "visualizations", "plugins", "skills", "rules", "secrets", "sandbox", "node_modules" };
    public static (Eligibility Eligibility, RemovalMethod Method, string Reason) Classify(CleanupRoot root, string relative, DateTime lastWriteUtc, DateTime nowUtc, bool tracked, bool generated)
    {
        var parts = relative.Split(['\\', '/'], StringSplitOptions.None);
        var name = Path.GetFileName(relative).ToLowerInvariant();
        var extension = Path.GetExtension(name);
        if (IsProtected(parts, name, extension, tracked))
        {
            return (Eligibility.Protected, RemovalMethod.None, "Source, credentials, tracked content, or persistent application data.");
        }

        if (root.Kind == RootKind.ReviewOnly)
        {
            return (Eligibility.Protected, RemovalMethod.None, "No supported cleanup rule proves this installation is obsolete.");
        }

        if ((!generated || root.Kind is RootKind.Temporary or RootKind.ProtectedSystem) && SourceExtensions.Contains(extension))
        {
            return (Eligibility.Protected, RemovalMethod.None, "Source or configuration is not disposable temporary data.");
        }

        if (lastWriteUtc > nowUtc || lastWriteUtc == default)
        {
            return (Eligibility.ReviewRequired, RemovalMethod.Recycle, "Timestamp requires review.");
        }

        if (root.Kind is RootKind.Temporary or RootKind.ProtectedSystem)
        {
            return nowUtc - lastWriteUtc >= TimeSpan.FromDays(7) ? (Eligibility.Eligible, RemovalMethod.Permanent, "Windows temporary files at least seven days old; revalidated before removal.") : (Eligibility.Protected, RemovalMethod.None, "Recent temporary data.");
        }

        if (root.Kind == RootKind.Logs)
        {
            return ClassifyLog(extension, nowUtc - lastWriteUtc);
        }

        if (IsRetainedArtifact(parts))
        {
            return (Eligibility.ReviewRequired, RemovalMethod.Recycle, "Release, evidence, or backup artifact; review individually.");
        }

        return generated ? (Eligibility.Eligible, RemovalMethod.Recycle, "Recognized generated project output.") : (Eligibility.ReviewRequired, RemovalMethod.Recycle, "Artifact origin or retained value needs individual review.");
    }
    private static bool IsProtected(string[] parts, string name, string extension, bool tracked) =>
        tracked || parts.Any(ProtectedParts.Contains) || name is "auth.json" or "config.toml" or "settings.json" || name.StartsWith(".env", StringComparison.Ordinal) ||
            extension is ".jks" or ".keystore" or ".pfx" or ".p12" or ".pem" or ".key" or ".db" or ".sqlite" or ".sqlite3" || name.EndsWith("-wal", StringComparison.Ordinal) || name.EndsWith("-shm", StringComparison.Ordinal);

    private static (Eligibility, RemovalMethod, string) ClassifyLog(string extension, TimeSpan age)
    {
        var minimumAge = TimeSpan.FromDays(extension == ".dmp" ? 7 : 14);
        if (extension is ".log" or ".dmp" && age >= minimumAge)
        {
            return (Eligibility.Eligible, RemovalMethod.Recycle, "Old log or crash dump; contents were not inspected.");
        }

        return (Eligibility.Protected, RemovalMethod.None, "Recent log or persistent state.");
    }
    private static bool IsRetainedArtifact(string[] parts) => parts.Any(p => p.Contains("release", StringComparison.OrdinalIgnoreCase) || p.Contains("signed", StringComparison.OrdinalIgnoreCase) || p.Contains("evidence", StringComparison.OrdinalIgnoreCase) || p.Contains("backup", StringComparison.OrdinalIgnoreCase));
}
