using System.Runtime.InteropServices;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Models;
using UpKeep_App.Platform;

namespace UpKeep.Tests;

public class NativeRecycleIntegrationTests
{
    [Fact]
    public async Task NativeRecycleMovesOnlyTheFixtureAndRestoresIt()
    {
        using var scratch = new Scratch();
        var name = "upkeep-recycle-" + Guid.NewGuid().ToString("N") + ".tmp";
        var path = scratch.Write(name, "disposable fixture");
        var identity = PathGuard.Snapshot(path);
        var root = new CleanupRoot("fixture", scratch.Root, RootKind.Project, CleanupCategory.TestArtifacts, PathGuard.Snapshot(scratch.Root));
        var candidate = new CleanupCandidate("fixture", "fixture", name, CleanupCategory.TestArtifacts, "fixture", identity.Length, identity, Eligibility.Eligible, RemovalMethod.Recycle, "", "fixture");
        var validation = PathGuard.Validate(root, candidate);
        Assert.True(validation.Valid, validation.Reason);
        using (var opened = validation.File!)
        {
            var result = await new RecycleBinService().RecycleAsync([opened], TestContext.Current.CancellationToken);
            Assert.Equal("Recycled", Assert.Single(result).State);
            Assert.False(File.Exists(path));
        }
        // Restore only the uniquely named fixture; never empty or alter other Recycle Bin items.
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                dynamic folder = ((dynamic)shell!).NameSpace(10);
                dynamic items = folder.Items();
                for (int i = 0; i < (int)items.Count; i++)
                {
                    dynamic item = items.Item(i);
                    try
                    {
                        if ((string)item.Name == name && string.Equals((string)item.ExtendedProperty("System.Recycle.DeletedFrom"), scratch.Root, StringComparison.OrdinalIgnoreCase))
                        {
                            item.InvokeVerb("undelete");
                            break;
                        }
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                Marshal.ReleaseComObject(items);
                Marshal.ReleaseComObject(folder);
                restored.SetResult();
            }
            catch (Exception e) { restored.SetException(e); }
            finally
            {
                if (shell is not null)
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await restored.Task;
        for (int i = 0; i < 50 && !File.Exists(path); i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), "The exact recycled test fixture must be restored before cleanup.");
        Assert.Equal("disposable fixture", File.ReadAllText(path));
    }
}
