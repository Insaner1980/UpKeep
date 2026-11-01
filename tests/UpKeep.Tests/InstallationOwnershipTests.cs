using UpKeep.Core.Discovery;
using UpKeep.Core.Models;

namespace UpKeep.Tests;

public class InstallationOwnershipTests
{
    private static UpdateItem Item(string provider, string identity, string name = "uv") => new(new(provider, "Windows", "pkg", identity), name, "1", "2", UpdateState.UpdateAvailable, "");
    [Fact] public void NpmCodexAndDesktopCodexRemainSeparate() => Assert.Equal(2, InstallationOwnership.Resolve([Item("npm", @"C:\npm\codex", "Codex"), Item("winget", @"C:\apps\codex", "Codex")]).Count);
    [Fact] public void DistinctUvInstallationsRemainSeparate() => Assert.Equal(2, InstallationOwnership.Resolve([Item("pip", @"C:\python\uv"), Item("winget", @"C:\tools\uv")]).Count);
    [Fact]
    public void SameExecutableUsesWinGetOwnerWithoutDoubleUpdate()
    {
        var result = InstallationOwnership.Resolve([Item("native", @"C:\tools\deno.exe"), Item("winget", @"c:\TOOLS\DENO.exe")]);
        var item = Assert.Single(result);
        Assert.Equal("winget", item.Key.ProviderId);
    }
    [Fact] public void WslInheritedWindowsCommandIsNotLinuxInstallation() => Assert.False(InstallationOwnership.IsLinuxInstallation("/mnt/c/Users/User/npm/codex.exe"));
    [Fact] public void CodexPrivatePowerShellIsNotHostPowerShellOwner() => Assert.False(ToolLocator.IsHostPath(@"C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\powershell\pwsh.exe"));
    [Fact] public void StalePythonRegistrationIsUnavailable() => Assert.False(InstallationOwnership.IsAvailableInterpreter(@"C:\nonexistent-upkeep-test\python.exe"));
    [Fact]
    public void NativeUpdaterInsideWinGetRootIsNotASecondOwner()
    {
        var native = new UpdateItem(new("bun", "Windows", "Bun", @"C:\Tools\Bun\bun.exe||Bun"), "Bun", "1", null, UpdateState.CheckUnavailable, "", UpdateKind.RunUpdater, new(ManagerKind.Bun, new(@"C:\Tools\Bun\bun.exe", [], @"C:\Tools\Bun\bun.exe")));
        var package = Item("winget", @"C:\Tools\Bun") with
        {
            Key = new("winget", "Windows:User", "Oven-sh.Bun", @"C:\Tools\Bun")
        };
        Assert.Equal(UpdateState.Blocked, InstallationOwnership.Resolve([native, package]).Single(i => i.Key.ProviderId == "bun").State);
    }
    [Theory]
    [InlineData(ManagerKind.CodeExtension, "editor-extension")]
    [InlineData(ManagerKind.DotNetTool, "global-tool")]
    [InlineData(ManagerKind.Bun, "global-package")]
    public void ManagedPackagesAreSeparateFromTheirHostInstaller(ManagerKind manager, string name)
    {
        var tool = new UpdateItem(new("manager", "Windows", name, @"C:\Tools\host.exe|" + name), name, "1", null, UpdateState.CheckUnavailable, "", UpdateKind.RunUpdater, new(manager, new(@"C:\Tools\host.exe", [], @"C:\Tools\host.exe")));
        var host = Item("winget", @"C:\Tools") with
        {
            Key = new("winget", "Windows:User", "Host", @"C:\Tools")
        };
        Assert.True(InstallationOwnership.Resolve([tool, host]).Single(i => i.Key.ProviderId == "manager").CanRun);
    }
}
