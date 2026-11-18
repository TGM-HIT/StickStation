using System.Globalization;
using System.Text;

namespace StickStation.Core;

/// <summary>Serializes appends from all concurrent stick workers to StickStation.log.</summary>
public static class OperationLog
{
    public const string FileName = "StickStation.log";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task AppendAsync(string directory, string stick, string message)
    {
        var path = Path.Combine(directory, FileName);
        var clean = message.Replace("\r\n", " | ").Replace("\n", " | ");
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} [{stick}] {clean}\n");
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = new UTF8Encoding(false).GetBytes(line);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new StickException(StickErrorCode.LogCreateFailed, "Cannot create the operation log in the backup directory.");
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
