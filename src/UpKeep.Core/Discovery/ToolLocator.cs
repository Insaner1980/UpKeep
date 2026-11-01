using Microsoft.Win32;

namespace UpKeep.Core.Discovery;

public sealed record ToolEntry(string Executable, IReadOnlyList<string> PrefixArguments, string Identity, IReadOnlyDictionary<string, string>? Environment = null);
public sealed class ToolLocator(IReadOnlyList<string>? searchDirectories = null)
{
    public static bool IsHostPath(string path) => !path.Contains("codex-runtimes", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\.codex\\", StringComparison.OrdinalIgnoreCase);
    public IEnumerable<string> SearchDirectories()
    {
        if (searchDirectories is not null)
        {
            return searchDirectories;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new[] { Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine), Environment.GetEnvironmentVariable("PATH") };
        return paths.SelectMany(p => (p ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Concat(new[] { Path.Combine(home,".local","bin"), Path.Combine(home,".bun","bin"), Path.Combine(local,"sonarqube-cli","bin"),
                Path.Combine(local,"Programs","coderabbit"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"AndroidCLI"), Path.Combine(home,"AppData","AndroidCLI"),
                Path.Combine(local,"Programs","Microsoft VS Code","bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PowerShell","7") })
            .Select(p => Environment.ExpandEnvironmentVariables(p.Trim('"'))).Where(p => Path.IsPathFullyQualified(p) && IsHostPath(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
    public ToolEntry? Find(string name)
    {
        var matches = FindAll(name);
        return matches.Count == 0 ? null : matches[0];
    }
    public IReadOnlyList<ToolEntry> FindAll(string name)
    {
        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new ArgumentException("Invalid tool name.");
        }

        var results = new List<ToolEntry>();
        if (searchDirectories is null)
        {
            AddRegisteredTools(name, results);
        }

        foreach (var directory in SearchDirectories())
        {
            AddDirectoryTools(directory, name, results);
        }

        if (name == "code")
        {
            results = results.Select(tool =>
        {
            var script = Path.Combine(Path.GetDirectoryName(tool.Executable)!, "resources", "app", "out", "cli.js");
            return File.Exists(script) ? tool with
            {
                PrefixArguments = [script],
                Environment = new Dictionary<string, string> { { "ELECTRON_RUN_AS_NODE", "1" } }
            } : tool;
        }).Where(tool => tool.PrefixArguments.Count > 0).ToList();
        }

        return results.DistinctBy(e => e.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public IReadOnlyList<string> RegisteredPythonInterpreters()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (searchDirectories is not null)
        {
            return [];
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadPythonRegistrations(hive, view, results);
            }
        }
        return results.ToArray();
    }
    private static void AddRegisteredTools(string name, List<ToolEntry> results)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                AddRegisteredTool(hive, view, name, results);
        }
    }

    private static void AddRegisteredTool(RegistryHive hive, RegistryView view, string name, List<ToolEntry> results)
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var key = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name + ".exe");
            if (key?.GetValue("") is not string registered)
                return;
            var path = Environment.ExpandEnvironmentVariables(registered.Trim('"'));
            if (Path.IsPathFullyQualified(path) && File.Exists(path) && IsHostPath(path))
                results.Add(new(path, [], path));
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Optional registry entries may be inaccessible; continue discovery elsewhere.
        }
    }
    private void AddDirectoryTools(string directory, string name, List<ToolEntry> results)
    {

        var executable = Path.Combine(directory, name + ".exe");
        if (File.Exists(executable))
        {
            try
            {
                executable = new FileInfo(executable).ResolveLinkTarget(true)?.FullName ?? executable;
            }
            catch (IOException)
            { // This optional registration or link is unreadable; continue with other discovery sources.
            }
            if (IsHostPath(executable))
            {
                results.Add(new(executable, [], executable));
            }
        }
        if (name is "npm" or "pnpm")
        {
            var script = Path.Combine(directory, "node_modules", name, "bin", name == "npm" ? "npm-cli.js" : "pnpm.cjs");
            var node = Find("node");
            if (File.Exists(script) && node is not null)
            {
                results.Add(new(node.Executable, [script], script));
            }
        }
        AddCodeTool(directory, name, results);
    }
    private static void ReadPythonRegistrations(RegistryHive hive, RegistryView view, HashSet<string> results)
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var python = registry.OpenSubKey(@"Software\Python");
            if (python is null)
            {
                return;
            }

            foreach (var company in python.GetSubKeyNames())
            {
                using var vendor = python.OpenSubKey(company);
                if (vendor is null)
                {
                    continue;
                }

                foreach (var tag in vendor.GetSubKeyNames())
                {
                    AddPythonInterpreter(vendor, tag, results);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { // This optional registration or link is unreadable; continue with other discovery sources.
        }
    }
    private static void AddCodeTool(string directory, string name, List<ToolEntry> results)
    {
        if (name == "code")
        {
            var root = Path.GetDirectoryName(directory)!;
            var code = Path.Combine(root, "Code.exe");
            var script = Path.Combine(root, "resources", "app", "out", "cli.js");
            if (File.Exists(code) && File.Exists(script))
            {
                results.Add(new(code, [script], code, new Dictionary<string, string> { { "ELECTRON_RUN_AS_NODE", "1" } }));
            }
        }
    }
    private static void AddPythonInterpreter(RegistryKey vendor, string tag, HashSet<string> results)
    {
        using var install = vendor.OpenSubKey(tag + @"\InstallPath");
        var executable = install?.GetValue("ExecutablePath") as string;
        if (executable is null && install?.GetValue("") is string root)
        {
            executable = Path.Combine(root, "python.exe");
        }

        if (executable is not null && Path.IsPathFullyQualified(executable) && IsHostPath(executable))
        {
            results.Add(executable);
        }
    }
}
