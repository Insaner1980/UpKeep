using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;

namespace UpKeep.Core.Updates;

internal static partial class WinGetActivation
{
    // Public WinGet out-of-process COM classes, as used by Microsoft's WinGet PowerShell engine.
    public static PackageManager Manager() => Create("C53A4F16-787E-42A4-B304-29EFFB4BF597", PackageManager.FromAbi);
    public static CreateCompositePackageCatalogOptions Composite() => Create("526534B8-7E46-47C8-8416-B1685C327D37", CreateCompositePackageCatalogOptions.FromAbi);
    public static FindPackagesOptions Find() => Create("572DED96-9C60-4526-8F92-EE7D91D38C1A", FindPackagesOptions.FromAbi);
    public static InstallOptions Install() => Create("1095F097-EB96-453B-B4E6-1613637F3B14", InstallOptions.FromAbi);
    private static T Create<T>(string id, Func<nint, T> project)
    {
        var clsid = Guid.Parse(id);
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 4, ref iid, out var pointer));
        try
        {
            return project(pointer);
        }
        finally { Marshal.Release(pointer); }
    }
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
}
