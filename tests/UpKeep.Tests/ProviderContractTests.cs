using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class ProviderContractTests
{
    [Theory]
    [InlineData("1.0", null)]
    [InlineData("2.0", "2.0")]
    public void NpmArrayInventoryUsesLatestFallbackAndDoesNotOfferCurrentVersion(string latest, string? expected)
    {
        var installed = "[{\"dependencies\":{\"fixture\":{\"version\":\"1.0\"}}}]";
        var outdated = "{\"fixture\":{\"current\":\"1.0\",\"latest\":\"" + latest + "\"}}";
        Assert.Equal(new PackageVersion("fixture", "1.0", expected), Assert.Single(ProviderParser.Npm(installed, outdated, 0)));
    }

    [Theory]
    [InlineData("[]", "{}")]
    [InlineData("[{},{}]", "{}")]
    [InlineData("{\"dependencies\":{\"fixture\":{\"version\":\"1.0\"}}}", "{\"fixture\":{\"current\":\"changed\",\"wanted\":\"2.0\"}}")]
    [InlineData("{\"dependencies\":{\"fixture\":{\"version\":\"1.0\"}}}", "{\"fixture\":{\"current\":\"1.0\",\"wanted\":\" \"}}")]
    public void AmbiguousOrChangedNpmInventoryCannotProduceAnUpdate(string installed, string outdated)
    {
        Assert.Throws<InvalidDataException>(() => ProviderParser.Npm(installed, outdated, 0));
    }

    [Fact]
    public void MalformedAndroidAndExtensionRowsAreNotAcceptedAsPackages()
    {
        Assert.Throws<InvalidDataException>(() => ProviderParser.Android("Installed packages:\nunrecognized row"));
        Assert.Throws<InvalidDataException>(() => ProviderParser.Extensions("missing-version"));
        Assert.Throws<InvalidDataException>(() => ProviderParser.Extensions("../outside@1.0"));
    }
    [Fact]
    public void NpmExitOneMeansUpdatesWhenJsonIsValid()
    {
        var rows = ProviderParser.Npm("{\"dependencies\":{\"@openai/codex\":{\"version\":\"1.0.0\"}}}", "{\"@openai/codex\":{\"current\":\"1.0.0\",\"wanted\":\"1.1.0\",\"latest\":\"1.1.0\"}}", 1);
        Assert.Equal(new PackageVersion("@openai/codex", "1.0.0", "1.1.0"), Assert.Single(rows));
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"error\":{\"code\":\"E401\"}}")]
    [InlineData("{\"dependencies\":[]}")]
    public void MalformedOrAuthenticationOutputIsUnavailable(string input) => Assert.ThrowsAny<Exception>(() => ProviderParser.Npm(input, "{}", 0));
    [Fact] public void EmptyGlobalPackagesAreValid() => Assert.Empty(ProviderParser.Npm("{}", "{}", 0));
    [Fact] public void UnexpectedNpmExitIsFailure() => Assert.ThrowsAny<Exception>(() => ProviderParser.Npm("{}", "{}", 2));
    [Fact]
    public void PipKeepsInterpreterSpecificInventory()
    {
        var result = ProviderParser.Pip("[{\"name\":\"uv\",\"version\":\"0.5.1\"}]", "[{\"name\":\"uv\",\"version\":\"0.5.1\",\"latest_version\":\"0.6.0\",\"latest_filetype\":\"wheel\"}]");
        Assert.Equal("0.6.0", Assert.Single(result).Latest);
    }
    [Fact] public void UvOutdatedFixturePreservesToolIdentity() => Assert.Equal(new PackageVersion("semgrep", "1.182.1", "1.183.0"), Assert.Single(ProviderParser.Uv("semgrep v1.182.1 [latest: 1.183.0]\n- pysemgrep\n- semgrep\n")));
    [Fact] public void AndroidEmulatorPackageIsNotAnAvd() => Assert.Equal(new PackageVersion("emulator", "37.2.12", "37.2.13"), Assert.Single(ProviderParser.Android("Installed packages:\n  emulator    37.2.12 -> 37.2.13  Android Emulator\n")));
    [Fact] public void LocalizedAndroidOutputIsNotEmptySuccess() => Assert.ThrowsAny<Exception>(() => ProviderParser.Android("Asennetut paketit:\n"));
    [Fact] public void ExtensionVersionIsSeparatedFromPublisherId() => Assert.Equal(new PackageVersion("ms-python.python", "2026.20.0"), Assert.Single(ProviderParser.Extensions("ms-python.python@2026.20.0\n")));
    [Theory]
    [InlineData("-danger")]
    [InlineData("x;whoami")]
    [InlineData("../outside")]
    [InlineData("$(command)")]
    [InlineData("a&b")]
    public void UntrustedPackageFragmentsAreRejected(string value) => Assert.False(ProviderParser.ValidPackageId(value));
}
