using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using UpKeep.Core.Elevation;

namespace UpKeep.CleanupHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || !args[0].StartsWith("UpKeep-", StringComparison.Ordinal) || args[0].Length > 100 || args[1].Length != 64 || !int.TryParse(args[2], out var parentId) || !long.TryParse(args[3], out var parentTicks))
        {
            return 2;
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return 3;
        }

        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != parentTicks || !string.Equals(parent.MainModule?.FileName, Path.Combine(AppContext.BaseDirectory, "UpKeep.App.exe"), StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await pipe.ConnectAsync(deadline.Token);
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != parentId)
            {
                return 5;
            }

            while (pipe.IsConnected)
            {
                ProtectedTempRequest request;
                try
                {
                    request = await CleanupProtocol.ReadAsync<ProtectedTempRequest>(pipe, CancellationToken.None);
                }
                catch (EndOfStreamException) { break; }
                CleanupProtocol.Validate(request, args[1], parentId, parentTicks);
                if (parent.HasExited)
                {
                    return 6;
                }

                await ExecuteRequestAsync(pipe, request, CancellationToken.None);
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { return 7; }
    }

    private static async Task ExecuteRequestAsync(NamedPipeClientStream pipe, ProtectedTempRequest request, CancellationToken token)
    {
        if (request.Action == ProtectedAction.Analyze)
        {
            int unavailable = 0;
            foreach (var batch in ProtectedTempService.Analyze(request.RootId, _ => unavailable++).Chunk(500))
            {
                await CleanupProtocol.WriteAsync(pipe, new ProtectedTempResult("Analyzing", batch, [], "", false), token);
            }

            await CleanupProtocol.WriteAsync(pipe, new ProtectedTempResult(unavailable == 0 ? "Completed" : "Partial", [], [],
                unavailable == 0 ? "Protected temporary analysis completed." : $"Partial analysis: {unavailable} inaccessible entries were skipped."), token);
        }
        else
        {
            await CleanupProtocol.WriteAsync(pipe, new ProtectedTempResult("Completed", [], ProtectedTempService.Remove(request), "Protected temporary removal completed."), token);
        }
    }
}
internal static partial class Native
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int pid);
}
