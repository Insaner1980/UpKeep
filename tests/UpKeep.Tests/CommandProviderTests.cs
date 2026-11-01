using System.Text.Json;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;
using UpKeep.Core.Updates;

namespace UpKeep.Tests;

public class CommandProviderTests
{
    [Fact]
    public async Task WindowsPowerShellFallbackUsesReadOnlyInventoryAndTruffleHogHasNoUpdater()
    {
        using var scratch = new Scratch();
        var runner = new Runner(_ => new(0, "{\"items\":[]}", ""));
        var result = await new CommandProvider(ManagerKind.PowerShell).CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken);
        Assert.True(result.Complete);
        Assert.Equal("powershell.exe", Path.GetFileName(Assert.Single(runner.Requests).Executable));
        Assert.All(runner.Requests, r => Assert.Equal(ExecutionKind.Probe, r.Kind));
        scratch.Write("trufflehog.exe", "");
        var item = Assert.Single((await new CommandProvider(ManagerKind.TruffleHog).CheckAsync(new(new ToolLocator([scratch.Root]), new Runner(_ => new(0, "1.0", "")), scratch.Root), TestContext.Current.CancellationToken)).Items);
        Assert.Equal(UpdateState.UnsupportedInstallation, item.State);
        Assert.False(item.CanRun);
    }
    private sealed class Runner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Theory]
    [InlineData(ManagerKind.Npm, "npm")]
    [InlineData(ManagerKind.Pnpm, "pnpm")]
    [InlineData(ManagerKind.Pip, "py")]
    [InlineData(ManagerKind.Python, "py")]
    [InlineData(ManagerKind.Uv, "uv")]
    [InlineData(ManagerKind.Bun, "bun")]
    [InlineData(ManagerKind.DotNetTool, "dotnet")]
    [InlineData(ManagerKind.VisualStudio, "vswhere")]
    [InlineData(ManagerKind.Wsl, "wsl")]
    [InlineData(ManagerKind.CodeExtension, "code")]
    [InlineData(ManagerKind.PowerShell, "pwsh")]
    [InlineData(ManagerKind.Claude, "claude")]
    [InlineData(ManagerKind.CodeRabbit, "coderabbit")]
    [InlineData(ManagerKind.Sonar, "sonar")]
    [InlineData(ManagerKind.AndroidCli, "android")]
    public async Task ReviewedInventoryProducesScopedMutationAndVerifiesVersion(ManagerKind kind, string command)
    {
        using var scratch = new Scratch();
        scratch.Write(command + ".exe", "");
        var python = scratch.Write("python.exe", "");
        if (kind == ManagerKind.CodeExtension)
        {
            scratch.Write("resources/app/out/cli.js", "");
        }

        bool updated = false;
        var runner = new Runner(request =>
        {
            if (request.Kind == ExecutionKind.Mutation)
            {
                updated = true;
                return new(0, "", "");
            }
            var version = updated ? "2.0" : "1.0";
            var args = request.Arguments;
            string output = kind switch
            {
                ManagerKind.Npm or ManagerKind.Pnpm when args.Contains("prefix") || args.Contains("root") || args.Contains("config") => scratch.Root,
                ManagerKind.Npm or ManagerKind.Pnpm when args.Contains("outdated") => updated ? "{}" : "{\"fixture\":{\"current\":\"1.0\",\"wanted\":\"2.0\"}}",
                ManagerKind.Npm or ManagerKind.Pnpm => JsonSerializer.Serialize(new { dependencies = new { fixture = new { version } } }),
                ManagerKind.Pip or ManagerKind.Python when request.Executable == python => args.Contains("--outdated") ? (updated ? "[]" : "[{\"name\":\"fixture\",\"latest_version\":\"2.0\"}]") : JsonSerializer.Serialize(new[] { new { name = "fixture", version } }),
                ManagerKind.Pip or ManagerKind.Python => JsonSerializer.Serialize(new { versions = new[] { new { executable = python, tag = "3.14", sort_version = version } } }).Replace("sort_version", "sort-version"),
                ManagerKind.Uv => updated ? "fixture v2.0" : "fixture v1.0 [latest: 2.0]",
                ManagerKind.Bun when args.Contains("pm") => "└── fixture@" + version,
                ManagerKind.DotNetTool when args.Contains("--list-sdks") => "10.0 [fixture]",
                ManagerKind.DotNetTool => JsonSerializer.Serialize(new { data = new[] { new { packageId = "fixture", version } } }),
                ManagerKind.VisualStudio => JsonSerializer.Serialize(new[] { new { installationPath = scratch.Root, instanceId = "fixture", installationVersion = version, displayName = "Fixture IDE" } }),
                ManagerKind.Wsl when args.Contains("--list") => "FixtureDistro",
                ManagerKind.CodeExtension => "publisher.fixture@" + version,
                ManagerKind.PowerShell => JsonSerializer.Serialize(new { items = new[] { new { name = "fixture", current = version, latest = updated ? null : "2.0", location = scratch.Root, error = (string?)null, repository = "Fixture" } } }),
                _ => version
            };
            return new(0, output, "");
        });
        var provider = new CommandProvider(kind);
        var inventory = await provider.CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken);
        Assert.True(inventory.Complete);
        var item = inventory.Items.First(i => i.CanRun);
        var result = await provider.UpdateAsync(item.Request(), new Progress<OperationProgress>(), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.CompletedUnverified, result.State);
        var mutation = Assert.Single(runner.Requests, r => r.Kind == ExecutionKind.Mutation);
        Assert.Equal(scratch.Root, mutation.WorkingDirectory);
        Assert.Equal(item.Metadata!.Tool.Executable, mutation.Executable);
        Assert.DoesNotContain("--all", mutation.Arguments);
        Assert.True((await provider.VerifyAsync(item.Request(), TestContext.Current.CancellationToken)).Verified);
    }

    [Theory]
    [InlineData(0, UpdateState.CompletedUnverified)]
    [InlineData(3010, UpdateState.RestartRequired)]
    [InlineData(1223, UpdateState.Canceled)]
    [InlineData(5, UpdateState.Failed)]
    public async Task MutationExitCodesPreserveTheirMeaning(int exitCode, UpdateState expected)
    {
        using var scratch = new Scratch();
        scratch.Write("claude.exe", "");
        var runner = new Runner(r => r.Kind == ExecutionKind.Mutation ? new(exitCode, "", "") : new(0, "1.0", ""));
        var provider = new CommandProvider(ManagerKind.Claude);
        var item = Assert.Single((await provider.CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken)).Items);
        Assert.Equal(expected, (await provider.UpdateAsync(item.Request(), new Progress<OperationProgress>(), TestContext.Current.CancellationToken)).State);
        Assert.False((await provider.VerifyAsync(item.Request(), TestContext.Current.CancellationToken)).Verified);
    }

    [Theory]
    [InlineData(ManagerKind.Npm, "npm")]
    [InlineData(ManagerKind.Uv, "uv")]
    [InlineData(ManagerKind.Bun, "bun")]
    [InlineData(ManagerKind.DotNetTool, "dotnet")]
    [InlineData(ManagerKind.Pip, "py")]
    [InlineData(ManagerKind.PowerShell, "pwsh")]
    public async Task MalformedInventoryCannotEnableMutation(ManagerKind kind, string command)
    {
        using var scratch = new Scratch();
        scratch.Write(command + ".exe", "");
        var runner = new Runner(_ => new(0, "unrecognized inventory", ""));
        var provider = new CommandProvider(kind);
        var result = await provider.CheckAsync(new(new ToolLocator([scratch.Root]), runner, scratch.Root), TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.All(result.Items, item => Assert.False(item.CanRun));
        Assert.DoesNotContain(runner.Requests, r => r.Kind == ExecutionKind.Mutation);
    }

    [Fact]
    public async Task UnreviewedRequestsAndUninitializedVerificationAreRejected()
    {
        var provider = new CommandProvider(ManagerKind.Npm);
        var request = new UpdateRequest(new("npm", "Windows", "fixture", "fixture"), "1", "2", UpdateKind.ExactPackage, null);
        Assert.Equal(UpdateState.Blocked, (await provider.UpdateAsync(request, new Progress<OperationProgress>(), TestContext.Current.CancellationToken)).State);
        Assert.False((await provider.VerifyAsync(request, TestContext.Current.CancellationToken)).Verified);
    }
}
