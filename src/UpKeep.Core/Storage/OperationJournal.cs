using System.Text.Json;

namespace UpKeep.Core.Storage;

public sealed record OperationRecord(string Kind, string State, int Total, int Completed, string Summary);
public sealed class OperationJournal(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Root, "last-operation.json");
    public void Write(OperationRecord record)
    {
        if (record.Summary.Length > 4096)
        {
            record = record with
            {
                Summary = record.Summary[..4096]
            };
        }

        AtomicFile.Write(FilePath, JsonSerializer.Serialize(record));
    }
    public OperationRecord? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            if (new FileInfo(FilePath).Length > 65536)
            {
                throw new InvalidDataException();
            }

            var record = JsonSerializer.Deserialize<OperationRecord>(File.ReadAllText(FilePath));
            return record?.State is "Running" or "Pending" ? record with
            {
                State = "Interrupted"
            } : record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new("Unknown", "Interrupted", 0, 0, "The previous operation summary could not be read. Check the results before trying again.");
        }
    }
}
