using UpKeep.Core.Elevation;
using UpKeep.Core.Models;

namespace UpKeep.Tests;

public class CleanupProtocolTests
{
    [Theory]
    [InlineData("WindowsTemp")]
    [InlineData("SystemTemp")]
    [InlineData("LocalServiceTemp")]
    [InlineData("NetworkServiceTemp")]
    public async Task AllowedRequestsRoundTripWithoutLosingIdentity(string root)
    {
        var request = Request() with
        {
            RootId = root
        };
        using var stream = new MemoryStream();
        await CleanupProtocol.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var restored = await CleanupProtocol.ReadAsync<ProtectedTempRequest>(stream, TestContext.Current.CancellationToken);
        CleanupProtocol.Validate(restored, request.Nonce, request.ParentPid, request.ParentStartTicks);
        Assert.Equal(root, restored.RootId);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), CleanupProtocol.ResolveRoot(root));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("sub/./file")]
    [InlineData("file:stream")]
    [InlineData("C:\\outside")]
    public void ProtectedRemovalRejectsEscapingCandidates(string path)
    {
        var candidate = new CleanupCandidate("fixture", "WindowsTemp", path, CleanupCategory.TemporaryFiles, "ProtectedTemp", 1, new(1, 2, 1, 3, 4, 1), Eligibility.Eligible, RemovalMethod.Permanent, "Fixture", "fixture");
        Assert.Throws<InvalidDataException>(() => CleanupProtocol.Validate(Request() with { Candidates = [candidate] }, "test-nonce", 42, 123));
        var valid = candidate with
        {
            RelativePath = "sub/file.tmp"
        };
        CleanupProtocol.Validate(Request() with
        {
            Candidates = [valid]
        }, "test-nonce", 42, 123);
        Assert.Throws<InvalidDataException>(() => CleanupProtocol.Validate(Request() with { Candidates = [valid with { Method = RemovalMethod.Recycle }] }, "test-nonce", 42, 123));
        Assert.Throws<InvalidDataException>(() => CleanupProtocol.Validate(Request() with { Candidates = [valid with { RuleId = "Other" }] }, "test-nonce", 42, 123));
    }

    [Fact]
    public async Task NullPayloadTruncationAndOversizedWritesAreRejected()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtocol.WriteAsync(stream, new string('a', CleanupProtocol.MaximumBytes), TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Length);
        await CleanupProtocol.WriteAsync<string?>(stream, null, TestContext.Current.CancellationToken);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtocol.ReadAsync<ProtectedTempRequest>(stream, TestContext.Current.CancellationToken));
        using var truncated = new MemoryStream([4, 0, 0, 0, 123]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => CleanupProtocol.ReadAsync<ProtectedTempRequest>(truncated, TestContext.Current.CancellationToken));
    }
    private static ProtectedTempRequest Request() => new("WindowsTemp", ProtectedAction.Analyze, "test-nonce", 42, 123, []);
    [Theory]
    [InlineData(@"C:\Users\User")]
    [InlineData("Registry")]
    [InlineData("exe")]
    [InlineData("../Temp")]
    public void ArbitraryRootsCannotBeRepresented(string root) => Assert.Throws<InvalidDataException>(() => CleanupProtocol.Validate(Request() with { RootId = root }, "test-nonce", 42, 123));
    [Fact]
    public void ForgedParentAndNonceAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => CleanupProtocol.Validate(Request(), "different", 42, 123));
        Assert.Throws<InvalidDataException>(() => CleanupProtocol.Validate(Request(), "test-nonce", 43, 123));
        Assert.Throws<InvalidDataException>(() => CleanupProtocol.Validate(Request(), "test-nonce", 42, 124));
    }
    [Fact]
    public async Task OversizedMessageAndDisconnectAreRejected()
    {
        using var bytes = new MemoryStream(BitConverter.GetBytes(CleanupProtocol.MaximumBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => CleanupProtocol.ReadAsync<ProtectedTempRequest>(bytes, TestContext.Current.CancellationToken));
        using var empty = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() => CleanupProtocol.ReadAsync<ProtectedTempRequest>(empty, TestContext.Current.CancellationToken));
    }
}
