namespace UpKeep.Tests;

internal sealed partial class Scratch : IDisposable
{
    private readonly string workspace;
    private readonly string scratchDirectory;
    public string Root
    {
        get;
    }
    public Scratch(string? startDirectory = null)
    {
        var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UpKeep.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Test workspace not found.");
        }

        workspace = directory.FullName;
        scratchDirectory = Path.Combine(workspace, "artifacts", "scratch");
        Root = Path.Combine(scratchDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }
    public string Write(string name, string value)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
        return path;
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (!string.Equals(Path.GetDirectoryName(resolved), scratchDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        {
            throw new InvalidOperationException("Unsafe test cleanup.");
        }
        for (var directory = new DirectoryInfo(resolved); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Reparse points are not allowed in the fixture cleanup path.");
            }
            if (string.Equals(directory.FullName, workspace, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
        DeleteTree(new DirectoryInfo(resolved));
    }
    private static void DeleteTree(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return;
        }

        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if (item is DirectoryInfo child && !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteTree(child);
            }
            else
            {
                item.Delete();
            }
        }
        directory.Delete();
    }
}
