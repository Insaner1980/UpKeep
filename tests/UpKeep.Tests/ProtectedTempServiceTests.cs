using System.Security.AccessControl;
using System.Security.Principal;
using UpKeep.CleanupHost;

namespace UpKeep.Tests;

public class ProtectedTempServiceTests
{
    [Fact]
    public void InaccessibleDirectoryDoesNotDiscardReadableCandidates()
    {
        using var scratch = new Scratch();
        var file = scratch.Write("temp/readable/old.tmp", "fixture");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-10));
        scratch.Write("temp/blocked/old.tmp", "fixture");
        var blocked = new DirectoryInfo(Path.Combine(scratch.Root, "temp", "blocked"));
        var security = blocked.GetAccessControl();
        var denial = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        security.AddAccessRule(denial);
        try
        {
            blocked.SetAccessControl(security);
            Assert.Throws<UnauthorizedAccessException>(() => blocked.GetFileSystemInfos());
            var unavailable = new List<string>();
            var candidates = ProtectedTempService.AnalyzeRoot("WindowsTemp", Path.Combine(scratch.Root, "temp"), unavailable.Add).ToArray();
            Assert.EndsWith("old.tmp", Assert.Single(candidates).RelativePath);
            Assert.Equal(blocked.FullName, Assert.Single(unavailable));
        }
        finally
        {
            var restored = blocked.GetAccessControl();
            restored.RemoveAccessRuleSpecific(denial);
            blocked.SetAccessControl(restored);
        }
    }

    [Fact]
    public void UnreadableRootIsReportedAsUnavailable()
    {
        using var scratch = new Scratch();
        var missing = Path.Combine(scratch.Root, "missing");
        var unavailable = new List<string>();
        Assert.Empty(ProtectedTempService.AnalyzeRoot("WindowsTemp", missing, unavailable.Add));
        Assert.Equal(missing, Assert.Single(unavailable));
    }
}
