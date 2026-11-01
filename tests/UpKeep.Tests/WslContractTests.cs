using UpKeep.Core.Updates;
namespace UpKeep.Tests;

public class WslContractTests
{
    [Fact] public void AptUsesManagerReportedDebianVersions() => Assert.Equal(new PackageVersion("curl", "8.5.0-2ubuntu10.5", "8.5.0-2ubuntu10.6"), Assert.Single(WslDistroProvider.ParseApt("Listing...\ncurl/noble-updates 8.5.0-2ubuntu10.6 amd64 [upgradable from: 8.5.0-2ubuntu10.5]\n")));
    [Fact] public void ChangedLocalizedAptOutputIsUnavailable() => Assert.Throws<InvalidDataException>(() => WslDistroProvider.ParseApt("Luetellaan..."));
}
