using System.Diagnostics;
using UpKeep.Core.Models;
using UpKeep.Core.Storage;
using UpKeep.Core.Updates;

namespace UpKeep.Core.Cleanup;

public sealed class CleanupAnalyzer(AppPaths paths, DiscoveryContext context)
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", ".idea", ".vs", ".codex", "worktrees", "sessions", "memories_v2", "generated_images", "attachments", "visualizations" };
    [Flags]
    private enum ProjectKinds
    {
        None = 0, DotNet = 1, Android = 2, Web = 4
    }
    private static readonly HashSet<string> ReviewDirectories = new(StringComparer.OrdinalIgnoreCase) { "reports", "output", "artifacts", ".native-artifacts" };
    public Task<CleanupAnalysis> AnalyzeAsync(CleanupScope scope, IProgress<CleanupProgress> progress, CancellationToken token)
        => Task.Run(() => new AnalysisSession(paths, context, progress, token).RunAsync(scope), token);

    private sealed record ScanNode(string Path, ProjectKinds Project, string? Output, bool Generated, HashSet<string>? Tracked);

    private sealed class AnalysisSession(AppPaths paths, DiscoveryContext context, IProgress<CleanupProgress> progress, CancellationToken token)
    {
        private static bool GeneratedDirectory(ProjectKinds project, string name) =>
            project.HasFlag(ProjectKinds.DotNet) && name is "bin" or "obj" or "TestResults" ||
            project.HasFlag(ProjectKinds.Android) && name == "build" ||
            project.HasFlag(ProjectKinds.Web) && name is "dist" or "build" or ".next" or "coverage" or "playwright-report" or "test-results";
        private readonly CleanupManifest manifest = new(paths);
        private readonly List<CleanupRoot> roots = [];
        private readonly Dictionary<string, CleanupGroup> groups = [];
        private readonly List<string> warnings = [];
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int count;

        public async Task<CleanupAnalysis> RunAsync(CleanupScope scope)
        {
            try
            {
                foreach (var root in scope.Roots)
                {
                    await AnalyzeRootAsync(root);
                }

                manifest.Seal();
                return new(manifest, roots, groups.Values.ToArray(), warnings.Distinct().Take(200).ToArray());
            }
            catch { manifest.Dispose(); throw; }
        }

        private void MarkUnavailable(CleanupRoot root, string location)
        {
            warnings.Add("Could not fully inspect: " + location);
            var id = root.Id + "|" + location + "|Unavailable";
            groups[id] = new(id, location, root.Category, 0, 0, Eligibility.Unavailable, "Location could not be fully inspected; size unknown.");
        }

        private async Task AnalyzeRootAsync(CleanupRoot original)
        {
            token.ThrowIfCancellationRequested();
            if (CleanupRootResolver.IsProtectedPath(original.Path))
            {
                original = original with
                {
                    Kind = RootKind.ProtectedSystem,
                    Category = CleanupCategory.TemporaryFiles
                };
            }
            if (original.Kind is RootKind.ProtectedSystem or RootKind.ReviewOnly)
            {
                groups[original.Id] = new(original.Id, original.Path, original.Category, 0, 0,
                    original.Kind == RootKind.ProtectedSystem ? Eligibility.Unavailable : Eligibility.Protected,
                    original.Kind == RootKind.ProtectedSystem ? "Administrator access required. Use Analyze system temporary files." : "Owner-managed cache or runtime. No blanket filesystem removal is available.");
                return;
            }
            try
            {
                var root = original with
                {
                    Identity = PathGuard.Snapshot(original.Path)
                };
                roots.Add(root);
                var pending = new Stack<ScanNode>();
                pending.Push(new(root.Path, ProjectKinds.None, null, false, null));
                while (pending.TryPop(out var node))
                {
                    await AnalyzeDirectoryAsync(root, node, pending);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MarkUnavailable(original, original.Path);
            }
        }

        private async Task AnalyzeDirectoryAsync(CleanupRoot root, ScanNode node, Stack<ScanNode> pending)
        {
            token.ThrowIfCancellationRequested();
            if (CleanupRootResolver.IsProtectedPath(node.Path))
            {
                groups[root.Id + "|" + node.Path] = new(root.Id + "|" + node.Path, node.Path,
                    CleanupCategory.TemporaryFiles, 0, 0, Eligibility.Unavailable,
                    "Administrator access required. Use Analyze system temporary files.");
                return;
            }
            FileSystemInfo[] entries;
            try
            {
                if (File.GetAttributes(node.Path).HasFlag(FileAttributes.ReparsePoint))
                {
                    return;
                }

                entries = new DirectoryInfo(node.Path).GetFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                MarkUnavailable(root, node.Path);
                return;
            }
            var project = DetectProject(entries, node.Project);
            var tracked = entries.Any(e => e.Name == ".git") ? await ReadTrackedAsync(node.Path) : node.Tracked;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    QueueDirectory(root, node, entry, project, tracked, pending);
                }
                else
                {
                    AnalyzeFile(root, node, entry, tracked);
                }
            }
        }

        private static ProjectKinds DetectProject(FileSystemInfo[] entries, ProjectKinds inherited)
        {
            var project = ProjectKinds.None;
            foreach (var name in entries.OfType<FileInfo>().Select(file => file.Name))
            {
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    project |= ProjectKinds.DotNet;
                }

                if (name is "build.gradle" or "build.gradle.kts" or "settings.gradle.kts")
                {
                    project |= ProjectKinds.Android;
                }

                if (name == "package.json")
                {
                    project |= ProjectKinds.Web;
                }
            }
            return project == ProjectKinds.None ? inherited : project;
        }

        private async Task<HashSet<string>?> ReadTrackedAsync(string directory)
        {
            var git = context.Tools.Find("git");
            if (git is null)
            {
                return null;
            }

            var result = await context.Runner.RunAsync(new(git.Executable, ["-C", directory, "ls-files", "--cached", "-z"], context.WorkingDirectory), null, token);
            return result.Success ? result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.GetFullPath(Path.Combine(directory, p))).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        }

        private static void QueueDirectory(CleanupRoot root, ScanNode node, FileSystemInfo entry, ProjectKinds project, HashSet<string>? tracked, Stack<ScanNode> pending)
        {
            if (SkipDirectories.Contains(entry.Name))
            {
                return;
            }

            var generated = node.Generated || GeneratedDirectory(project, entry.Name);
            var output = node.Output;
            if ((root.Kind == RootKind.Project && output is null && project != ProjectKinds.None && (generated || ReviewDirectories.Contains(entry.Name))) ||
                (output == node.Path && ReviewDirectories.Contains(Path.GetFileName(node.Path))))
            {
                output = entry.FullName;
            }

            pending.Push(new(entry.FullName, project, output, generated, tracked));
        }

        private void AnalyzeFile(CleanupRoot root, ScanNode node, FileSystemInfo entry, HashSet<string>? tracked)
        {
            if (root.Kind == RootKind.Project && node.Output is null)
            {
                return;
            }

            if (entry.FullName.StartsWith(Path.TrimEndingDirectorySeparator(paths.Root) + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var relative = Path.GetRelativePath(root.Path, entry.FullName);
            var policy = CleanupPolicy.Classify(root, relative, entry.LastWriteTimeUtc, DateTime.UtcNow, tracked?.Contains(entry.FullName) == true, node.Generated,
                node.Output is null ? null : Path.GetRelativePath(node.Output, entry.FullName));
            if (policy.Eligibility == Eligibility.Protected)
            {
                return;
            }

            if (root.Kind == RootKind.Project && tracked is null && policy.Eligibility == Eligibility.Eligible)
            {
                policy = (Eligibility.ReviewRequired, RemovalMethod.Recycle, "Git status could not be established; review individually.");
            }

            try
            {
                var identity = PathGuard.Snapshot(entry.FullName);
                var group = root.Id + "|" + (node.Output ?? root.Path) + "|" + policy.Eligibility;
                var candidate = new CleanupCandidate(Guid.NewGuid().ToString("N"), root.Id, relative, root.Category, root.Kind.ToString(), identity.Length, identity, policy.Eligibility, policy.Method, policy.Reason, group);
                manifest.Append(candidate);
                count++;
                AddToGroup(candidate, node.Output ?? root.Path);
                if (clock.ElapsedMilliseconds >= 200)
                {
                    progress.Report(new(count, 0, 0, "Analyzing locations…"));
                    clock.Restart();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MarkUnavailable(root, node.Path);
            }
        }

        private void AddToGroup(CleanupCandidate candidate, string location)
        {
            if (groups.TryGetValue(candidate.Group, out var prior))
            {
                groups[candidate.Group] = prior with
                {
                    LogicalSize = prior.LogicalSize + candidate.LogicalSize,
                    Count = prior.Count + 1,
                    Eligibility = prior.Eligibility == candidate.Eligibility ? prior.Eligibility : Eligibility.ReviewRequired
                };
            }
            else
            {
                groups[candidate.Group] = new(candidate.Group, location, candidate.Category, candidate.LogicalSize, 1, candidate.Eligibility, candidate.Reason);
            }
        }
    }
}
