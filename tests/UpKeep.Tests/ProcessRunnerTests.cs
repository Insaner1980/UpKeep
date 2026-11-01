using System.Text.Json;
using UpKeep.Core.Processes;

namespace UpKeep.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task EnvironmentOverridesAndUtf16OutputArePreserved()
    {
        using var scratch = new Scratch();
        var request = Script(scratch, "[Console]::OutputEncoding=[Text.Encoding]::Unicode; [Console]::Out.Write($env:UPKEEP_FIXTURE_VALUE)") with
        {
            Environment = new Dictionary<string, string> { ["UPKEEP_FIXTURE_VALUE"] = "ä漢字 fixture" },
            Utf16Output = true
        };
        var result = await new ProcessRunner().RunAsync(request, null, TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal("ä漢字 fixture", result.StandardOutput.Trim('\ufeff'));
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotLaunchTheChild()
    {
        using var scratch = new Scratch();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var result = await new ProcessRunner().RunAsync(Script(scratch, "Set-Content -LiteralPath started.txt -Value started"), null, stop.Token);
        Assert.True(result.Canceled);
        Assert.Null(result.ExitCode);
        Assert.False(File.Exists(Path.Combine(scratch.Root, "started.txt")));
    }
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static ProcessRequest Script(Scratch scratch, string script, ExecutionKind kind = ExecutionKind.Probe, TimeSpan? timeout = null, params string[] args)
        => new(PowerShell, new[] { "-NoProfile", "-NonInteractive", "-File", scratch.Write("child.ps1", script) }.Concat(args).ToArray(), scratch.Root, kind, timeout);

    [Fact]
    public async Task ArgumentsArriveAsDataWithoutExecutingMetacharacters()
    {
        using var scratch = new Scratch();
        var values = new[] { "two words", "ä漢字", "it's", "a&b", "-leading", "$(not-a-command); test" };
        var request = Script(scratch, "[Console]::OutputEncoding=[Text.Encoding]::UTF8; ConvertTo-Json -Compress -InputObject @($args)", args: values);
        var result = await new ProcessRunner().RunAsync(request, null, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.StartFailure ?? result.StandardError);
        Assert.Equal(values, JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim('\ufeff', '\r', '\n')));
    }
    [Fact]
    public async Task DrainsBothStreamsAndRetainsAtMostOneMiB()
    {
        using var scratch = new Scratch();
        var result = await new ProcessRunner().RunAsync(Script(scratch, "for($i=0;$i -lt 10000;$i++){[Console]::Out.WriteLine(('x'*120));[Console]::Error.WriteLine(('e'*120))}"), null, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput + result.StandardError), 1, 1048576);
    }
    [Fact]
    public async Task PreservesNonzeroExit()
    {
        using var scratch = new Scratch();
        var result = await new ProcessRunner().RunAsync(Script(scratch, "[Console]::Error.Write('failed'); exit 17"), null, TestContext.Current.CancellationToken);
        Assert.Equal(17, result.ExitCode);
        Assert.Contains("failed", result.StandardError);
    }
    [Fact]
    public async Task MissingExecutableReturnsExplicitFailure()
    {
        using var scratch = new Scratch();
        var result = await new ProcessRunner().RunAsync(new(Path.Combine(scratch.Root, "missing.exe"), [], scratch.Root), null, TestContext.Current.CancellationToken);
        Assert.NotNull(result.StartFailure);
        Assert.False(result.Success);
    }
    [Fact]
    public async Task ProbeTimeoutStopsOwnedProcess()
    {
        using var scratch = new Scratch();
        var result = await new ProcessRunner().RunAsync(Script(scratch, "Start-Sleep -Seconds 20", timeout: TimeSpan.FromMilliseconds(400)), null, TestContext.Current.CancellationToken);
        Assert.True(result.Canceled);
    }
    [Fact]
    public async Task MutationTimeoutAndCancellationDoNotKillActiveInstaller()
    {
        using var scratch = new Scratch();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await new ProcessRunner().RunAsync(Script(scratch, "Start-Sleep -Seconds 2; [Console]::Out.Write('completed')", ExecutionKind.Mutation, TimeSpan.FromMilliseconds(300)), null, stop.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("completed", result.StandardOutput);
        Assert.False(result.Canceled);
    }
}
