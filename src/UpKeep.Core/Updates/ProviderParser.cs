using System.Text.Json;
using System.Text.RegularExpressions;

namespace UpKeep.Core.Updates;

public sealed record PackageVersion(string Id, string Current, string? Latest = null);
public static partial class ProviderParser
{
    public static IReadOnlyList<PackageVersion> Npm(string installed, string outdated, int exitCode)
    {
        if (exitCode is not 0 and not 1)
        {
            throw new InvalidDataException("Package check failed.");
        }

        using var inventory = JsonDocument.Parse(installed);
        using var updates = JsonDocument.Parse(outdated);
        var root = inventory.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            root = root.GetArrayLength() == 1 ? root[0] : throw new InvalidDataException("Ambiguous global installation.");
        }

        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) || updates.RootElement.ValueKind != JsonValueKind.Object || updates.RootElement.TryGetProperty("error", out _))
        {
            throw new InvalidDataException("Package inventory unavailable.");
        }

        if (!root.TryGetProperty("dependencies", out var dependencies))
        {
            return [];
        }

        var result = new List<PackageVersion>();
        foreach (var package in dependencies.EnumerateObject())
        {
            Validate(package.Name);
            var current = package.Value.GetProperty("version").GetString() ?? throw new InvalidDataException();
            var latest = NpmTarget(package.Name, current, updates.RootElement);
            result.Add(new(package.Name, current, latest));
        }
        return result;
    }
    public static IReadOnlyList<PackageVersion> Pip(string installed, string outdated)
    {
        using var inventory = JsonDocument.Parse(installed);
        using var updates = JsonDocument.Parse(outdated);
        var latest = updates.RootElement.EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("latest_version").GetString(), StringComparer.OrdinalIgnoreCase);
        return inventory.RootElement.EnumerateArray().Select(p => { var id = p.GetProperty("name").GetString()!; Validate(id); return new PackageVersion(id, p.GetProperty("version").GetString()!, latest.GetValueOrDefault(id)); }).ToArray();
    }
    public static string Normalize(string text) => AnsiEscapeRegex().Replace(text.Replace("\0", ""), "").Trim('\ufeff');
    public static IReadOnlyList<PackageVersion> Uv(string text)
    {
        var result = new List<PackageVersion>();
        foreach (var line in Normalize(text).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var match = UvPackageRegex().Match(line);
            if (!match.Success)
            {
                throw new InvalidDataException("Unrecognized uv tool output.");
            }

            result.Add(new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Success ? match.Groups[3].Value : null));
        }
        return result;
    }
    public static IReadOnlyList<PackageVersion> Android(string text)
    {
        var lines = Normalize(text).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "Installed packages:")
        {
            throw new InvalidDataException("Unrecognized Android SDK output.");
        }

        var result = new List<PackageVersion>();
        foreach (var line in lines.Skip(1))
        {
            var m = AndroidPackageRegex().Match(line);
            if (!m.Success)
            {
                throw new InvalidDataException("Unrecognized Android SDK package.");
            }

            result.Add(new(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null));
        }
        return result;
    }
    public static IReadOnlyList<PackageVersion> Extensions(string text) => Normalize(text).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line =>
    {
        var at = line.LastIndexOf('@');
        if (at <= 0)
        {
            throw new InvalidDataException("Unrecognized extension output.");
        }

        var id = line[..at];
        Validate(id);
        return new PackageVersion(id, line[(at + 1)..]);
    }).ToArray();
    public static bool ValidPackageId(string id) => id.Length <= 256 && PackageIdRegex().IsMatch(id);
    public static void Validate(string id)
    {
        if (!ValidPackageId(id))
        {
            throw new InvalidDataException("Unsupported package identifier.");
        }
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.None, 1000)]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9][a-zA-Z0-9._-]*) v([^\s]+)(?: \[latest: ([^\]]+)\])?$", RegexOptions.None, 1000)]
    private static partial Regex UvPackageRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9][a-zA-Z0-9/_.;-]*)\s+([0-9][^\s]*)(?:\s+->\s+([0-9][^\s]*))?\s+.+$", RegexOptions.None, 1000)]
    private static partial Regex AndroidPackageRegex();

    [GeneratedRegex(@"^(?:@[a-zA-Z0-9][a-zA-Z0-9._-]*/)?[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.None, 1000)]
    private static partial Regex PackageIdRegex();
    private static string? NpmTarget(string name, string current, JsonElement updates)
    {
        string? latest = null;
        if (updates.TryGetProperty(name, out var update))
        {
            if (update.GetProperty("current").GetString() != current)
            {
                throw new InvalidDataException("Installation changed during the check.");
            }

            latest = update.TryGetProperty("wanted", out var wanted) ? wanted.GetString() : update.GetProperty("latest").GetString();
            if (string.IsNullOrWhiteSpace(latest))
            {
                throw new InvalidDataException();
            }

            if (latest == current)
            {
                latest = null;
            }
        }
        return latest;
    }
}
