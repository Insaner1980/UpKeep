using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UpKeep.Core.Discovery;
using UpKeep.Core.Models;
using UpKeep.Core.Processes;

namespace UpKeep.Core.Updates;

public sealed partial class CommandProvider(ManagerKind manager) : IUpdateProvider
{
    private const string JsonFormat = "--format=json";
    private const string GlobalOption = "--global";
    private const string VersionOption = "--version";
    private const string UpdateCommand = "update";
    public string Id => manager.ToString().ToLowerInvariant();
    private DiscoveryContext? context;
    private Dictionary<InstallationKey, UpdateItem> reviewed = [];
    private async Task<ProcessResult> Run(ToolEntry tool, IReadOnlyList<string> arguments, CancellationToken token, ExecutionKind kind = ExecutionKind.Probe)
        => await context!.Runner.RunAsync(new(tool.Executable, tool.PrefixArguments.Concat(arguments).ToArray(), context.WorkingDirectory, kind, Environment: tool.Environment, Utf16Output: manager == ManagerKind.Wsl), null, token);
    private async Task<string> Probe(ToolEntry tool, CancellationToken token, params string[] arguments)
    {
        var r = await Run(tool, arguments, token);
        if (!r.Success)
        {
            throw new InvalidDataException(r switch
            {
                { Canceled: true } => "Check timed out or was canceled.",
                { Truncated: true } => "Provider output exceeded the capture limit.",
                _ => "Provider check failed (" + r.ExitCode + ")."
            });
        }

        return ProviderParser.Normalize(r.StandardOutput).Trim();
    }
    private UpdateItem Row(ToolEntry tool, PackageVersion package, string scope = "", UpdateKind kind = UpdateKind.ExactPackage, string? detail = null, UpdateState? state = null)
        => new(new(Id, scope.Length == 0 ? "Windows" : scope, package.Id, tool.Identity + "|" + scope + "|" + package.Id), package.Id, package.Current, package.Latest,
            state ?? (package.Latest is null ? UpdateState.UpToDate : UpdateState.UpdateAvailable), detail ?? manager + " / " + (scope.Length == 0 ? "Windows" : scope), kind, new(manager, tool, scope));
    public async Task<ProviderCheckResult> CheckAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        this.context = context;
        var rows = new List<UpdateItem>();
        bool complete = true;
        var tools = FindTools(context);
        foreach (var tool in tools)
        {
            try
            {
                complete &= await CheckToolAsync(tool, rows, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                complete = false;
                rows.Add(Row(tool, new(manager.ToString(), ""), state: UpdateState.CheckUnavailable, detail: e.Message));
            }
        }
        if (manager == ManagerKind.Pip)
        {
            foreach (var interpreter in context.Tools.RegisteredPythonInterpreters().Where(path => !rows.Any(r => string.Equals(r.Metadata?.Scope, path, StringComparison.OrdinalIgnoreCase))))
            {
                var python = new ToolEntry(interpreter, [], interpreter);
                try
                {
                    if (!InstallationOwnership.IsAvailableInterpreter(interpreter))
                    {
                        throw new InvalidDataException("Stale Python registration: executable unavailable.");
                    }

                    rows.AddRange(ProviderParser.Pip(await Probe(python, cancellationToken, "-m", "pip", "list", JsonFormat), await Probe(python, cancellationToken, "-m", "pip", "list", "--outdated", JsonFormat)).Select(p => Row(python, p, interpreter)));
                }
                catch (Exception e) when (e is not OperationCanceledException) { complete = false; rows.Add(Row(python, new("Registered Python", ""), interpreter, state: UpdateState.CheckUnavailable, detail: e.Message)); }
            }
        }

        rows = rows.DistinctBy(r => r.Key).ToList();
        reviewed = rows.ToDictionary(r => r.Key);
        return new(rows, complete);
    }
    private async Task<bool> CheckToolAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
        => manager switch
        {
            ManagerKind.Npm or ManagerKind.Pnpm => await CheckPnpmAsync(tool, rows, cancellationToken),
            ManagerKind.Pip or ManagerKind.Python => await CheckPythonAsync(tool, rows, cancellationToken),
            ManagerKind.Uv => await CheckUvAsync(tool, rows, cancellationToken),
            ManagerKind.AndroidSdk => await CheckAndroidSdkAsync(tool, rows, cancellationToken),
            ManagerKind.Bun => await CheckBunAsync(tool, rows, cancellationToken),
            ManagerKind.Claude or ManagerKind.CodeRabbit or ManagerKind.Sonar or ManagerKind.AndroidCli => await CheckAndroidCliAsync(tool, rows, cancellationToken),
            ManagerKind.DotNetTool => await CheckDotNetToolAsync(tool, rows, cancellationToken),
            ManagerKind.VisualStudio => await CheckVisualStudioAsync(tool, rows, cancellationToken),
            ManagerKind.Wsl => await CheckWslAsync(tool, rows, cancellationToken),
            ManagerKind.CodeExtension => await CheckCodeExtensionAsync(tool, rows, cancellationToken),
            ManagerKind.PowerShell => await CheckPowerShellAsync(tool, rows, cancellationToken),
            ManagerKind.TruffleHog => await CheckTruffleHogAsync(tool, rows, cancellationToken),
            _ => true
        };

    private async Task<bool> CheckPnpmAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        bool complete = true;
        var effective = tool;
        if (manager == ManagerKind.Pnpm)
        {
            var home = Environment.GetEnvironmentVariable("PNPM_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = await Probe(tool, cancellationToken, "config", "get", "global-bin-dir");
            }

            var bins = new[] { home, home is not null ? Path.Combine(home, "bin") : null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "bin") }.Where(p => p is not null && Path.IsPathFullyQualified(p) && Directory.Exists(p));
            effective = tool with
            {
                Environment = new Dictionary<string, string> { { "PATH", string.Join(";", bins) + ";" + Environment.GetEnvironmentVariable("PATH") } }
            };
        }
        var scope = await Probe(effective, cancellationToken, manager == ManagerKind.Npm ? "prefix" : "root", GlobalOption);
        if (!Path.IsPathFullyQualified(scope))
        {
            throw new InvalidDataException("Global package root unavailable.");
        }

        var installed = await Probe(effective, cancellationToken, "list", GlobalOption, "--depth=0", "--json");
        var outdated = await Run(effective, ["outdated", GlobalOption, "--json"], cancellationToken);
        if (outdated.Canceled || outdated.Truncated)
        {
            throw new InvalidDataException("Incomplete outdated result.");
        }

        rows.AddRange(ProviderParser.Npm(installed, string.IsNullOrWhiteSpace(outdated.StandardOutput) && outdated.ExitCode == 0 ? "{}" : outdated.StandardOutput, outdated.ExitCode ?? -1).Select(p => Row(effective, p, scope)));
        return complete;
    }

    private async Task<bool> CheckPythonAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        bool complete = true;
        using var json = JsonDocument.Parse(await Probe(tool, cancellationToken, "list", JsonFormat));
        foreach (var version in json.RootElement.GetProperty("versions").EnumerateArray())
        {
            var path = version.GetProperty("executable").GetString()!;
            var tag = version.GetProperty("tag").GetString()!;
            if (!InstallationOwnership.IsAvailableInterpreter(path))
            {
                rows.Add(Row(tool, new(tag, ""), path, state: UpdateState.UnsupportedInstallation, detail: "Stale Python registration: executable unavailable."));
                continue;
            }
            if (manager == ManagerKind.Python)
            {
                rows.Add(Row(tool, new(tag, version.GetProperty("sort-version").GetString()!), path, UpdateKind.RunUpdater, "Update this managed Python version only; preserves other Python versions.", UpdateState.CheckUnavailable));
                continue;
            }
            var python = new ToolEntry(path, [], path);
            try
            {
                rows.AddRange(ProviderParser.Pip(await Probe(python, cancellationToken, "-m", "pip", "list", JsonFormat), await Probe(python, cancellationToken, "-m", "pip", "list", "--outdated", JsonFormat)).Select(p => Row(python, p, path)));
            }
            catch (InvalidDataException e) { rows.Add(Row(python, new("pip", ""), path, state: UpdateState.CheckUnavailable, detail: e.Message)); complete = false; }
        }
        return complete;
    }

    private async Task<bool> CheckUvAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        rows.AddRange(ProviderParser.Uv(await Probe(tool, cancellationToken, "tool", "list", "--outdated", "--color", "never")).Select(p => Row(tool, p, "uv tools", detail: "uv tool upgrade preserves the tool's installed version constraints.")));
        return true;
    }

    private async Task<bool> CheckAndroidSdkAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        if (!Directory.Exists(sdk))
        {
            throw new InvalidDataException("Android SDK location unavailable.");
        }

        rows.AddRange(ProviderParser.Android(await Probe(tool, cancellationToken, "--sdk=" + sdk, "sdk", "list")).Select(p => Row(tool, p, sdk, detail: "Android SDK package; virtual-device data is separate. Licenses are not accepted automatically.")));
        return true;
    }

    private async Task<bool> CheckBunAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        var inventory = await Probe(tool, cancellationToken, "pm", "ls", "-g");
        var matches = BunPackageRegex().Matches(inventory);
        if (matches.Count == 0 && !inventory.Contains("(0 installed)", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unrecognized Bun global inventory.");
        }

        foreach (var groups in matches.Select(match => match.Groups))
        {
            ProviderParser.Validate(groups[1].Value);
            rows.Add(Row(tool, new(groups[1].Value, groups[2].Value), "Bun globals", UpdateKind.RunUpdater, "Run the owning manager's selected-package updater.", UpdateState.CheckUnavailable));
        }
        rows.Add(Row(tool, new("Bun", await Probe(tool, cancellationToken, VersionOption)), kind: UpdateKind.RunUpdater, state: UpdateState.CheckUnavailable, detail: "Bun's built-in stable updater."));
        return true;
    }

    private async Task<bool> CheckAndroidCliAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        rows.Add(Row(tool, new(manager.ToString(), await Probe(tool, cancellationToken, VersionOption)), kind: UpdateKind.RunUpdater, state: UpdateState.CheckUnavailable, detail: "Built-in updater. Latest version is not verified; run only this installation's updater."));
        return true;
    }

    private async Task<bool> CheckDotNetToolAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await Probe(tool, cancellationToken, "tool", "list", GlobalOption, "--format", "json"));
        foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = item.GetProperty("packageId").GetString()!;
            ProviderParser.Validate(id);
            rows.Add(Row(tool, new(id, item.GetProperty("version").GetString()!), ".NET global tools", UpdateKind.RunUpdater, "Updates only this global tool using its configured NuGet sources.", UpdateState.CheckUnavailable));
        }
        var sdks = await Probe(tool, cancellationToken, "--list-sdks");
        foreach (var line in sdks.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            rows.Add(Row(tool, new(".NET SDK " + line.Trim(), ""), "SDK registrations", state: UpdateState.CheckUnavailable, detail: "Managed by its Windows installer: use the matching WinGet or Visual Studio installation. No project SDK pins will change."));
        }

        return true;
    }

    private async Task<bool> CheckVisualStudioAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await Probe(tool, cancellationToken, "-all", "-products", "*", "-format", "json", "-utf8"));
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var path = item.GetProperty("installationPath").GetString()!;
            var setup = Path.Combine(Path.GetDirectoryName(tool.Executable)!, "setup.exe");
            rows.Add(Row(new(setup, [], setup), new(item.GetProperty("instanceId").GetString()!, item.GetProperty("installationVersion").GetString()!), path, UpdateKind.RunUpdater, item.GetProperty("displayName").GetString() + ". Updates this instance without adding workloads.", UpdateState.CheckUnavailable));
        }
        return true;
    }

    private async Task<bool> CheckWslAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        rows.Add(Row(tool, new("Windows WSL", await Probe(tool, cancellationToken, VersionOption)), kind: UpdateKind.RunUpdater, state: UpdateState.CheckUnavailable, detail: "Updates Windows WSL. Distro packages are separate."));
        var distros = await Probe(tool, cancellationToken, "--list", "--quiet");
        foreach (var distro in distros.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            rows.Add(Row(tool, new(distro, ""), "WSL:" + distro, UpdateKind.CheckEnvironment, "Check " + distro + " starts the distribution. APT and Snap require a separate reviewed transaction.", UpdateState.CheckUnavailable));
        }

        return true;
    }

    private async Task<bool> CheckCodeExtensionAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        rows.AddRange(ProviderParser.Extensions(await Probe(tool, cancellationToken, "--list-extensions", "--show-versions")).Select(p => Row(tool, p, "VS Code default profile", UpdateKind.RunUpdater, "Updates only this extension in this editor's default profile.", UpdateState.CheckUnavailable)));
        return true;
    }

    private async Task<bool> CheckPowerShellAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        bool complete = true;
        var script = Path.Combine(AppContext.BaseDirectory, "Updates", "PowerShellResources.ps1");
        using var json = JsonDocument.Parse(await Probe(tool, cancellationToken, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", "List"));
        foreach (var module in json.RootElement.GetProperty("items").EnumerateArray())
        {
            var id = module.GetProperty("name").GetString()!;
            ProviderParser.Validate(id);
            var error = module.GetProperty("error").GetString();
            rows.Add(Row(tool, new(id, module.GetProperty("current").GetString()!, module.GetProperty("latest").GetString()), module.GetProperty("location").GetString()!, detail: error ?? "PowerShellGet / " + module.GetProperty("repository").GetString(), state: error is null ? null : UpdateState.CheckUnavailable));
            complete &= error is null;
        }
        return complete;
    }

    private async Task<bool> CheckTruffleHogAsync(ToolEntry tool, List<UpdateItem> rows, CancellationToken cancellationToken)
    {
        rows.Add(Row(tool, new("TruffleHog", await Probe(tool, cancellationToken, VersionOption)), state: UpdateState.UnsupportedInstallation, detail: "This manually located executable has no verified installation owner or supported self-update contract. Use its original installation source."));
        return true;
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (!reviewed.TryGetValue(request.Key, out var row) || row.Request() != request || !row.CanRun || request.Metadata is not { } metadata)
        {
            return new(UpdateState.Blocked, "Check this installation again before updating.");
        }

        var id = request.Key.PackageId;
        var target = request.TargetVersion;
        var scope = metadata.Scope;
        if (manager is ManagerKind.Npm or ManagerKind.Pnpm or ManagerKind.Pip or ManagerKind.Uv or ManagerKind.DotNetTool or ManagerKind.CodeExtension)
        {
            ProviderParser.Validate(id);
        }

        string[] args = manager switch
        {
            ManagerKind.Npm => ["install", GlobalOption, "--prefix", scope, id + "@" + target],
            ManagerKind.Pnpm => ["add", GlobalOption, id + "@" + target],
            ManagerKind.Pip => ["-m", "pip", "install", "--upgrade", id + "==" + target],
            ManagerKind.Uv => ["tool", "upgrade", id],
            ManagerKind.Python => ["install", "--update", id],
            ManagerKind.Bun when id == "Bun" => ["upgrade"],
            ManagerKind.Bun => [UpdateCommand, GlobalOption, id],
            ManagerKind.Claude or ManagerKind.CodeRabbit or ManagerKind.Sonar or ManagerKind.AndroidCli => [UpdateCommand],
            ManagerKind.AndroidSdk => ["--sdk=" + scope, "sdk", UpdateCommand, id, "--no-downgrade"],
            ManagerKind.DotNetTool => ["tool", UpdateCommand, GlobalOption, id],
            ManagerKind.VisualStudio => [UpdateCommand, "--installPath", scope, "--passive", "--norestart"],
            ManagerKind.Wsl when request.Kind == UpdateKind.RunUpdater => ["--update"],
            ManagerKind.CodeExtension => ["--install-extension", id, "--force"],
            ManagerKind.PowerShell => ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Updates", "PowerShellResources.ps1"), "-Action", "Update", "-Name", id, "-TargetVersion", target!, "-ReviewedPath", scope],
            _ => throw new InvalidOperationException("This operation is not supported.")
        };
        var result = await Run(metadata.Tool, args, cancellationToken, ExecutionKind.Mutation);
        return new(result.ExitCode switch
        {
            0 => UpdateState.CompletedUnverified,
            3010 => UpdateState.RestartRequired,
            1223 => UpdateState.Canceled,
            _ => UpdateState.Failed
        },
            result.StartFailure ?? "Manager exited with code " + result.ExitCode + ". Installed version will be checked.", result.ExitCode);
    }
    public async Task<VerificationResult> VerifyAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return new(false, null);
        }

        var results = await CheckAsync(context, cancellationToken);
        var item = results.Items.FirstOrDefault(i => i.Key == request.Key);
        var readable = item is not null && !string.IsNullOrWhiteSpace(item.CurrentVersion) && item.Kind == request.Kind;
        return new(readable && (request.TargetVersion is not null ? item!.CurrentVersion == request.TargetVersion : item!.CurrentVersion != request.CurrentVersion), item?.CurrentVersion);
    }

    [GeneratedRegex(@"(?m)^[├└]── ([^\s]+)@([^\s]+)\s*$", RegexOptions.None, 1000)]
    private static partial Regex BunPackageRegex();

    private List<ToolEntry> FindTools(DiscoveryContext context)
    {
        string command = manager switch
        {
            ManagerKind.Npm => "npm",
            ManagerKind.Pnpm => "pnpm",
            ManagerKind.Bun => "bun",
            ManagerKind.Pip or ManagerKind.Python => "py",
            ManagerKind.Uv => "uv",
            ManagerKind.Claude => "claude",
            ManagerKind.CodeRabbit => "coderabbit",
            ManagerKind.Sonar => "sonar",
            ManagerKind.AndroidCli or ManagerKind.AndroidSdk => "android",
            ManagerKind.DotNetTool => "dotnet",
            ManagerKind.Wsl or ManagerKind.Apt or ManagerKind.Snap => "wsl",
            ManagerKind.CodeExtension => "code",
            ManagerKind.PowerShell => "pwsh",
            _ => "vswhere"
        };
        var tools = context.Tools.FindAll(command).ToList();
        if (manager == ManagerKind.TruffleHog)
        {
            tools = context.Tools.FindAll("trufflehog").ToList();
        }

        if (manager == ManagerKind.PowerShell && tools.Count == 0)
        {
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(shell))
            {
                tools.Add(new(shell, [], shell));
            }
        }
        if (manager == ManagerKind.VisualStudio)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(path))
            {
                tools.Add(new(path, [], path));
            }
        }

        return tools;
    }
}
