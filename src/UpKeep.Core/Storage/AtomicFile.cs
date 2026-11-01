namespace UpKeep.Core.Storage;

internal static class AtomicFile
{
    public static void Write(string path, string content, Action<string, string>? writer = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; attempt <= 7; attempt++)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                (writer ?? File.WriteAllText)(temporary, content);
                File.Move(temporary, path, overwrite: true);
                return;
            }
            // Recreate the replacement after a transient Windows sharing/access failure; preserve the prior file.
            catch (Exception e) when (attempt < 7 && (e is IOException or UnauthorizedAccessException) && ((e.HResult & 0xffff) is 5 or 32 or 33))
            {
                Thread.Sleep(50 * (1 << attempt));
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
