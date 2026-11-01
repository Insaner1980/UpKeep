using System.Diagnostics;
using System.Text;

namespace UpKeep.Core.Processes;

public enum ExecutionKind
{
    Probe, Mutation
}
public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    ExecutionKind Kind = ExecutionKind.Probe, TimeSpan? Timeout = null, IReadOnlyDictionary<string, string>? Environment = null, bool Utf16Output = false);
public sealed record ProcessOutput(string Text, bool IsError);
public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool Truncated = false,
    bool Canceled = false, string? StartFailure = null)
{
    public bool Success => ExitCode == 0 && !Canceled && StartFailure is null && !Truncated;
}
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken cancellationToken);
}
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutput>? output, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(null, "", "", Canceled: true);
        }

        if (!ValidRequest(request))
        {
            return new(null, "", "", StartFailure: "A valid absolute executable and working directory are required.");
        }

        var info = CreateStartInfo(request);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                return new(null, "", "", StartFailure: "The process did not start.");
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new(null, "", "", StartFailure: error.Message);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sync = new object();
        var retained = 0;
        var truncated = false;
        // A UTF-16 character needs at most four UTF-8 bytes, so the combined capture cannot exceed 1 MiB.
        async Task Drain(StreamReader reader, StringBuilder destination)
        {
            var buffer = new char[4096];
            int read;
            // Drain to EOF even when queued work is canceled; an active mutation must finish.
            while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) != 0)
            {
                lock (sync)
                {
                    var take = Math.Min(read, 262144 - retained);
                    destination.Append(buffer, 0, take);
                    retained += take;
                    truncated |= take != read;
                }
            }
        }
        var drainOut = Drain(process.StandardOutput, stdout);
        var drainError = Drain(process.StandardError, stderr);
        bool canceled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Kind == ExecutionKind.Probe)
        {
            timeout.CancelAfter(request.Timeout ?? TimeSpan.FromSeconds(60));
        }

        try
        {
            await process.WaitForExitAsync(request.Kind == ExecutionKind.Probe ? timeout.Token : CancellationToken.None);
        }
        catch (OperationCanceledException) when (request.Kind == ExecutionKind.Probe)
        {
            canceled = true;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            { // The owned child exited between the state check and termination.
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(drainOut, drainError);
        return new(process.ExitCode, stdout.ToString(), stderr.ToString(), truncated, canceled);
    }
    private static bool ValidRequest(ProcessRequest request) =>
        Path.IsPathFullyQualified(request.Executable) && File.Exists(request.Executable) &&
        Path.IsPathFullyQualified(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory) &&
        Path.GetExtension(request.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = request.Utf16Output ? Encoding.Unicode : Encoding.UTF8,
            StandardErrorEncoding = request.Utf16Output ? Encoding.Unicode : Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        return info;
    }
}
