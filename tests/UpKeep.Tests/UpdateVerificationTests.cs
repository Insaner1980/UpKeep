using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class UpdateVerificationTests
{
    private sealed class Runner : IProcessRunner
    {
        public bool Fail;
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken token) => Task.FromResult(Fail ? new ProcessResult(7, "", "Check failed") : new ProcessResult(0, "1.0.0", ""));
    }
    [Theory]
    [InlineData(ManagerKind.Claude, "claude")]
    [InlineData(ManagerKind.Sonar, "sonar")]
    [InlineData(ManagerKind.CodeRabbit, "coderabbit")]
    public async Task FailedVersionProbeNeverVerifiesSuccessfulInstaller(ManagerKind kind, string command)
    {
        using var scratch = new Scratch();
        scratch.Write(command + ".exe", "");
        var runner = new Runner();
        var provider = new CommandProvider(kind);
        var item = Assert.Single((await provider.CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken)).Items);
        runner.Fail = true;
        Assert.False((await provider.VerifyAsync(item.Request(), TestContext.Current.CancellationToken)).Verified);
    }
}
