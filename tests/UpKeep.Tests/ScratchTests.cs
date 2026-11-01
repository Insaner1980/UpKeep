namespace UpKeep.Tests;

public class ScratchTests
{
    [Fact]
    public void FixturesInRenamedCheckoutsDisposeOnlyTheirOwnDirectory()
    {
        using var outer = new Scratch();
        outer.Write("renamed-checkout/UpKeep.slnx", "");
        var retained = outer.Write("renamed-checkout/retained.txt", "keep");
        string fixtureRoot;
        using (var fixture = new Scratch(Path.GetDirectoryName(retained)))
        {
            fixtureRoot = fixture.Root;
            fixture.Write("nested/fixture.txt", "fixture");
        }
        Assert.False(Directory.Exists(fixtureRoot));
        Assert.Equal("keep", File.ReadAllText(retained));
    }
}
