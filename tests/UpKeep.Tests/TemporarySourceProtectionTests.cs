using UpKeep.Core.Cleanup;
using UpKeep.Core.Models;

namespace UpKeep.Tests;

public class TemporarySourceProtectionTests
{
    [Theory]
    [InlineData("script.ps1")]
    [InlineData("project/Main.cs")]
    [InlineData("project/build.gradle.kts")]
    [InlineData("project/project.csproj")]
    [InlineData("config.ini")]
    public void OldTemporarySourceOrConfigurationIsProtected(string relative)
    {
        var result = CleanupPolicy.Classify(new("temp", @"C:\tmp", RootKind.Temporary, CleanupCategory.TemporaryFiles), relative, DateTime.UtcNow.AddDays(-10), DateTime.UtcNow, false, false);
        Assert.Equal(Eligibility.Protected, result.Eligibility);
        Assert.Equal(RemovalMethod.None, result.Method);
    }
}
