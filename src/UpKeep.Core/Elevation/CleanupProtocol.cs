using System.Diagnostics;
using System.Text.Json;
using UpKeep.Core.Cleanup;
using UpKeep.Core.Models;

namespace UpKeep.Core.Elevation;

public enum ProtectedAction
{
    Analyze, Remove
}
public sealed record ProtectedTempRequest(string RootId, ProtectedAction Action, string Nonce, int ParentPid, long ParentStartTicks, IReadOnlyList<CleanupCandidate> Candidates);
public sealed record ProtectedTempResult(string State, IReadOnlyList<CleanupCandidate> Candidates, IReadOnlyList<CleanupItemResult> Results, string Detail, bool Last = true);
public static class CleanupProtocol
{
    public const int MaximumBytes = 1048576;
    public static string ResolveRoot(string id)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return id switch
        {
            "WindowsTemp" => Path.Combine(windows, "Temp"),
            "SystemTemp" => Path.Combine(windows, "SystemTemp"),
            "LocalServiceTemp" => Path.Combine(windows, @"ServiceProfiles\LocalService\AppData\Local\Temp"),
            "NetworkServiceTemp" => Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Temp"),
            _ => throw new InvalidDataException("Unsupported system root.")
        };
    }
    public static void Validate(ProtectedTempRequest request, string nonce, int parentPid, long parentStartTicks)
    {
        _ = ResolveRoot(request.RootId);
        if (!Enum.IsDefined(request.Action) || request.Nonce != nonce || request.ParentPid != parentPid || request.ParentStartTicks != parentStartTicks || request.Candidates.Count > 1000)
        {
            throw new InvalidDataException("Invalid cleanup request identity or size.");
        }

        foreach (var candidate in request.Candidates)
        {
            if (candidate.RootId != request.RootId || Path.IsPathRooted(candidate.RelativePath) || candidate.RelativePath.Split(['\\', '/'], StringSplitOptions.None).Any(p => p is ".." or "." || p.Contains(':')) || candidate.Method != RemovalMethod.Permanent || candidate.RuleId != "ProtectedTemp")
            {
                throw new InvalidDataException("Unsupported protected candidate.");
            }
        }
    }
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("Cleanup message is too large.");
        }

        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BitConverter.ToInt32(header);
        if (length < 1 || length > MaximumBytes)
        {
            throw new InvalidDataException("Invalid cleanup message size.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Invalid cleanup message.");
    }
}
