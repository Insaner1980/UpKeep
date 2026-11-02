using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UpKeep.Core.Elevation;
using UpKeep.Core.Models;

namespace UpKeep_App.Platform;

public static partial class ElevationClient
{
    public static async Task<ProtectedTempResult> ExecuteAsync(string rootId, ProtectedAction action, IReadOnlyList<CleanupCandidate> candidates, CancellationToken token)
    {
        _ = CleanupProtocol.ResolveRoot(rootId);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pipeName = "UpKeep-" + Guid.NewGuid().ToString("N");
        using var parent = Process.GetCurrentProcess();
        var helperPath = Path.Combine(AppContext.BaseDirectory, "UpKeep.CleanupHost.exe");
        if (!File.Exists(helperPath))
        {
            return new("Blocked", [], [], "The adjacent cleanup helper is unavailable.");
        }

        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in new[] { pipeName, nonce, parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString() })
        {
            start.ArgumentList.Add(arg);
        }

        Process? helper = null;
        var all = new List<CleanupCandidate>();
        var results = new List<CleanupItemResult>();
        try
        {
            helper = Process.Start(start) ?? throw new IOException("Cleanup helper did not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            await server.WaitForConnectionAsync(deadline.Token);
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid) || pid != helper.Id)
            {
                throw new IOException("Cleanup helper identity mismatch.");
            }

            var batches = action == ProtectedAction.Analyze ? new[] { Array.Empty<CleanupCandidate>() } : candidates.Chunk(500).ToArray();
            foreach (var batch in batches)
            {
                await CleanupProtocol.WriteAsync(server, new ProtectedTempRequest(rootId, action, nonce, parent.Id, parent.StartTime.ToUniversalTime().Ticks, batch), token);
                ProtectedTempResult response;
                do
                {
                    response = await CleanupProtocol.ReadAsync<ProtectedTempResult>(server, token);
                    all.AddRange(response.Candidates);
                    results.AddRange(response.Results);
                    if (response.State is "Blocked" or "Failed" or "Partial")
                    {
                        return response with
                        {
                            Candidates = all,
                            Results = results
                        };
                    }
                } while (!response.Last);
            }
            return new("Completed", all, results, "Protected temporary operation finished.");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return new("Canceled", [], [], "Administrator request was canceled."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { return new("Blocked", all, results, e.Message); }
        finally
        {
            await server.DisposeAsync();
            await WaitForHelperAsync(helper);
        }
    }
    private static async Task WaitForHelperAsync(Process? helper)
    {
        if (helper is not null)
        {
            using (helper)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await helper.WaitForExitAsync(deadline.Token);
                }
                catch (OperationCanceledException) {/* Disconnect prevents further requests; never kill an in-flight file operation. */}
            }
        }
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int pid);
}
