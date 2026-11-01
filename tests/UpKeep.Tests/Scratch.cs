namespace UpKeep.Tests;

internal sealed partial class Scratch : IDisposable
{
    public string Root
    {
        get;
    }
    public Scratch()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UpKeep.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Test workspace not found.");
        }

        Root = Path.Combine(directory.FullName, "artifacts", "scratch", Guid.NewGuid().ToString("N"));
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
        if (!resolved.Contains(Path.Combine("UpKeep", "artifacts", "scratch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsafe test cleanup.");
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
