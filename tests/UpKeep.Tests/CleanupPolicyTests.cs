using UpKeep.Core.Cleanup;
using UpKeep.Core.Models;

namespace UpKeep.Tests;

public class CleanupPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static (Eligibility Eligibility, RemovalMethod Method, string Reason) Classify(string path, bool tracked = false, bool generated = true, RootKind kind = RootKind.Project, int days = 20)
        => CleanupPolicy.Classify(new("r", @"C:\Test", kind, CleanupCategory.TestArtifacts), path, Now.AddDays(-days), Now, tracked, generated);
    [Theory]
    [InlineData(@"bin\Debug\app.dll")]
    [InlineData(@"bin\Release\app.dll")]
    [InlineData(@"obj\Release\app.dll")]
    public void OrdinaryGeneratedOutputUsesRecycleBin(string path)
    {
        var r = Classify(path);
        Assert.Equal(Eligibility.Eligible, r.Eligibility);
        Assert.Equal(RemovalMethod.Recycle, r.Method);
    }
    [Theory]
    [InlineData(@"tests\test.cs")]
    [InlineData(@"androidTest\Test.kt")]
    [InlineData(@"auth.json")]
    [InlineData(@"state.db-wal")]
    [InlineData(@"worktrees\project\build\app.dll")]
    [InlineData(@"generated_images\image.png")]
    [InlineData(@"visualizations\chart.html")]
    [InlineData(@"attachments\note.txt")]
    [InlineData(@"plugins\plugin.dll")]
    [InlineData(@"signing.jks")]
    public void SourceCredentialsAndCodexDeliverablesAreProtected(string path) => Assert.Equal(Eligibility.Protected, Classify(path).Eligibility);
    [Fact] public void TrackedReportIsProtected() => Assert.Equal(Eligibility.Protected, Classify(@"reports\run\report.html", tracked: true).Eligibility);
    [Fact] public void SignedReleaseEvidenceRequiresReview() => Assert.Equal(Eligibility.ReviewRequired, Classify(@"reports\signed-release\release.apk").Eligibility);
    [Theory]
    [InlineData(@"bin\Release\release.apk")]
    [InlineData(@"bin\Release\releases\app.zip")]
    [InlineData(@"bin\Release\signed\app.dll")]
    public void RetainedArtifactsInsideReleaseBuildStillRequireReview(string path) => Assert.Equal(Eligibility.ReviewRequired, Classify(path).Eligibility);
    [Fact] public void UnknownOutputIsNotDisposable() => Assert.Equal(Eligibility.ReviewRequired, Classify(@"reports\notes.txt", generated: false).Eligibility);
    [Fact] public void PreviousRuntimeNameDoesNotProveObsolescence() => Assert.NotEqual(Eligibility.Eligible, Classify(@"previous-runtime\node.exe", kind: RootKind.ReviewOnly).Eligibility);
    [Theory]
    [InlineData(6, Eligibility.Protected)]
    [InlineData(7, Eligibility.Eligible)]
    [InlineData(-1, Eligibility.ReviewRequired)]
    public void TemporaryAgeBoundaryIsConservative(int days, Eligibility expected) => Assert.Equal(expected, Classify("old.tmp", kind: RootKind.Temporary, days: days).Eligibility);
    [Theory]
    [InlineData(13, Eligibility.Protected)]
    [InlineData(14, Eligibility.Eligible)]
    public void OldLogAgeBoundary(int days, Eligibility expected) => Assert.Equal(expected, Classify("old.log", kind: RootKind.Logs, days: days).Eligibility);
}
