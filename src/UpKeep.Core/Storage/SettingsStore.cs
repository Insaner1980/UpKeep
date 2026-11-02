using System.Text.Json;

namespace UpKeep.Core.Storage;

public sealed record AppSettings(int SchemaVersion, IReadOnlyList<string> ProjectRoots);
public sealed record SettingsLoad(AppSettings Settings, string? Warning = null);
public sealed class SettingsStore(AppPaths paths, Action<string, string>? writer = null)
{
    private string FilePath => Path.Combine(paths.Root, "settings.json");
    public SettingsLoad Load()
    {
        if (!File.Exists(FilePath))
        {
            return new(new(1, []));
        }

        try
        {
            if (new FileInfo(FilePath).Length > 65536)
            {
                throw new InvalidDataException();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (settings?.SchemaVersion != 1 || settings.ProjectRoots is null || settings.ProjectRoots.Any(p => !ValidRoot(p)))
            {
                throw new InvalidDataException();
            }

            return new(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new(new(1, []), "Project settings could not be read. The existing file has been preserved.");
        }
    }
    private static bool ValidRoot(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.StartsWith("\\\\", StringComparison.Ordinal) &&
        !string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase);
    public void Save(AppSettings settings)
    {
        if (Load().Warning is not null)
        {
            throw new InvalidDataException("Existing settings need manual review before replacement.");
        }

        if (settings.SchemaVersion != 1 || settings.ProjectRoots.Count > 100 || settings.ProjectRoots.Any(p => !ValidRoot(p)))
        {
            throw new InvalidDataException("Select local project folders, not drive roots.");
        }

        var content = JsonSerializer.Serialize(settings with
        {
            ProjectRoots = settings.ProjectRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        });
        if (System.Text.Encoding.UTF8.GetByteCount(content) > 65536)
        {
            throw new InvalidDataException("Project settings exceed the supported file size.");
        }
        AtomicFile.Write(FilePath, content, writer);
    }
}
