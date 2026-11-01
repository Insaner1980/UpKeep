using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UpKeep.Core.Models;

namespace UpKeep.Core.Cleanup;

public sealed record PathValidationResult(bool Valid, string Reason, ValidatedCleanupFile? File = null);
public sealed class ValidatedCleanupFile(string path, SafeFileHandle handle, IReadOnlyList<SafeFileHandle> parents, CleanupCandidate candidate) : IDisposable
{
    public string Path { get; } = path; public SafeFileHandle Handle { get; } = handle; public CleanupCandidate Candidate { get; } = candidate;
    public void Dispose()
    {
        Handle.Dispose();
        foreach (var parent in parents)
        {
            parent.Dispose();
        }
    }
}
public sealed partial class PathGuard
{
    public static bool ValidLocalRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Length < 4 || path[1] != ':' || path[2] != '\\' || path[2..].Contains(':'))
        {
            return false;
        }

        if (path.Split(['\\', '/'], StringSplitOptions.None).Any(p => p is ".." or "." || p.EndsWith(' ') || p.EndsWith('.')))
        {
            return false;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Length > 3 && new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed;
        }
        catch (Exception e) when (e is IOException or ArgumentException) { return false; }
    }
    private static SafeFileHandle Open(string path, uint access, uint share)
    {
        var handle = CreateFile(path, access, share, 0, 3, 0x00200000 | 0x02000000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            handle.Dispose();
            throw new Win32Exception();
        }
        if ((info.Attributes & 0x400) != 0)
        {
            handle.Dispose();
            throw new IOException("Reparse point is excluded.");
        }
        var resolved = new StringBuilder(32768);
        if (GetFinalPathNameByHandle(handle, resolved, (uint)resolved.Capacity, 0) == 0)
        {
            handle.Dispose();
            throw new Win32Exception();
        }
        var final = resolved.ToString();
        if (final.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            final = final[4..];
        }

        if (!string.Equals(Path.TrimEndingDirectorySeparator(final), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            throw new IOException("The resolved path differs from the analyzed path.");
        }
        return handle;
    }
    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var i))
        {
            throw new Win32Exception();
        }

        return new(i.Volume, ((ulong)i.IdHigh << 32) | i.IdLow, ((long)i.SizeHigh << 32) | i.SizeLow, ((long)i.WriteHigh << 32) | i.WriteLow, ((long)i.CreatedHigh << 32) | i.CreatedLow, i.Links);
    }
    public static FileIdentity Snapshot(string path)
    {
        using var handle = Open(path, 0x80, 7);
        return Identity(handle);
    }
    public static PathValidationResult Validate(CleanupRoot root, CleanupCandidate candidate)
    {
        var parents = new List<SafeFileHandle>();
        SafeFileHandle? file = null;
        try
        {
            if (!ValidLocalRoot(root.Path) || candidate.RootId != root.Id || Path.IsPathRooted(candidate.RelativePath) || candidate.RelativePath.Split(['\\', '/'], StringSplitOptions.None).Any(p => p is ".." or "." || p.Contains(':')))
            {
                throw new IOException("Invalid cleanup path.");
            }

            var path = Path.GetFullPath(Path.Combine(root.Path, candidate.RelativePath));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path)) + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Path is outside the analyzed root.");
            }

            OpenParents(path, root, parents);
            file = Open(path, candidate.Method == RemovalMethod.Permanent ? 0x10080u : 0x80u, candidate.Method == RemovalMethod.Permanent ? 1u : 5u);
            if (Identity(file) != candidate.Identity)
            {
                throw new IOException("Skipped: changed since analysis");
            }

            return new(true, "", new(path, file, parents, candidate));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            file?.Dispose();
            foreach (var parent in parents)
            {
                parent.Dispose();
            }

            return new(false, error is Win32Exception { NativeErrorCode: 32 } ? "Skipped: in use" : "Skipped: changed since analysis or unavailable");
        }
    }
    private static void OpenParents(string path, CleanupRoot root, List<SafeFileHandle> parents)
    {
        var directories = new Stack<string>();
        var parent = Path.GetDirectoryName(path);
        while (parent is not null)
        {
            directories.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }
        while (directories.TryPop(out var directory))
        {
            var opened = Open(directory, 0x80, 1);
            parents.Add(opened);
            if (string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(root.Path), StringComparison.OrdinalIgnoreCase) && root.Identity is { } expected)
            {
                var actual = Identity(opened);
                if (actual.Volume != expected.Volume || actual.Id != expected.Id || actual.Created != expected.Created)
                {
                    throw new IOException("Cleanup root changed since analysis.");
                }
            }
        }
    }
    public static void Delete(ValidatedCleanupFile file)
    {
        if (file.Candidate.Method != RemovalMethod.Permanent)
        {
            throw new InvalidOperationException("Removal method was not permanent.");
        }

        var disposition = new Disposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(file.Handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
        {
            throw new Win32Exception();
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IdHigh, IdLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition
    {
        public byte DeleteFile;
    }
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CreateFileW")] private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref Disposition information, uint size);
}
