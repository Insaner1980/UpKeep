namespace UpKeep.Core.Storage;

public sealed class AppPaths
{
    public string Root
    {
        get;
    }
    public string Temp => Path.Combine(Root, "Temp");
    public AppPaths(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpKeep");
}
