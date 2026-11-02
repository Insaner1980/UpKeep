using UpKeep.Core.Cleanup;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;

namespace UpKeep.Tests;

public class RootDiscoveryTests
{
    [Fact]
    public void ProjectFolderInsideSystemTempCannotBecomeOrdinaryCleanup()
    {
        var path = Path.Combine(UpKeep.Core.Elevation.CleanupProtocol.ResolveRoot("WindowsTemp"), "UpKeep-root-classification-only");
        var root = Assert.Single(CleanupRootResolver.Resolve([path]), r => r.Path == path);
        Assert.Equal(RootKind.ProtectedSystem, root.Kind);
        Assert.Equal(CleanupCategory.TemporaryFiles, root.Category);
    }

    [Fact]
    public void ScriptBasedNodeManagersAndEditorAreBoundToTheirOwningExecutable()
    {
        using var scratch = new Scratch();
        var node = scratch.Write("node.exe", "");
        var npm = scratch.Write("node_modules/npm/bin/npm-cli.js", "");
        var pnpm = scratch.Write("node_modules/pnpm/bin/pnpm.cjs", "");
        var code = scratch.Write("editor/Code.exe", "");
        var cli = scratch.Write("editor/resources/app/out/cli.js", "");
        Directory.CreateDirectory(Path.Combine(scratch.Root, "editor", "bin"));
        var locator = new ToolLocator([scratch.Root, Path.Combine(scratch.Root, "editor", "bin")]);
        Assert.Equal(node, locator.Find("npm")?.Executable);
        Assert.Equal(Path.GetFullPath(npm), Assert.Single(locator.Find("npm")!.PrefixArguments));
        Assert.Equal(Path.GetFullPath(pnpm), Assert.Single(locator.Find("pnpm")!.PrefixArguments));
        var editor = Assert.Single(locator.FindAll("code"));
        Assert.Equal(Path.GetFullPath(code), editor.Executable);
        Assert.Equal(Path.GetFullPath(cli), Assert.Single(editor.PrefixArguments));
        Assert.Equal("1", editor.Environment!["ELECTRON_RUN_AS_NODE"]);
    }
    [Fact]
    public void LocalRootsAreDeduplicatedAndKeepTheirSafetyClassification()
    {
        using var scratch = new Scratch();
        var roots = CleanupRootResolver.Resolve([scratch.Root, scratch.Root + "\\", "relative", "", @"\\server\share"]);
        var project = Assert.Single(roots, r => r.Kind == RootKind.Project);
        Assert.Equal(scratch.Root, project.Path);
        Assert.Equal(CleanupCategory.TestArtifacts, project.Category);
        Assert.Equal(roots.Count, roots.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(roots, r => Assert.True(PathGuard.ValidLocalRoot(r.Path)));
        foreach (var rootId in new[] { "WindowsTemp", "SystemTemp", "LocalServiceTemp", "NetworkServiceTemp" })
        {
            var path = UpKeep.Core.Elevation.CleanupProtocol.ResolveRoot(rootId);
            var root = Assert.Single(roots, r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(RootKind.ProtectedSystem, root.Kind);
            Assert.Equal(CleanupCategory.TemporaryFiles, root.Category);
        }
        Assert.All(roots.Where(r => r.Kind == RootKind.Logs), r => Assert.True(Directory.Exists(r.Path)));
    }

    [Fact]
    public void HostDiscoveryUsesOnlyQualifiedHostPathsAndReadsPythonRegistrations()
    {
        var locator = new ToolLocator();
        var directories = locator.SearchDirectories().ToArray();
        Assert.NotEmpty(directories);
        Assert.All(directories, path => Assert.True(Path.IsPathFullyQualified(path) && ToolLocator.IsHostPath(path)));
        Assert.All(locator.RegisteredPythonInterpreters(), path => Assert.True(Path.IsPathFullyQualified(path) && ToolLocator.IsHostPath(path)));
        Assert.All(locator.FindAll("dotnet"), tool => Assert.True(File.Exists(tool.Executable)));
        Assert.Throws<ArgumentException>(() => locator.Find("../invalid"));
    }
}
