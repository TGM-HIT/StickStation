using System.Security.Cryptography;

namespace StickStation.Core;

/// <summary>SHA-256 inventories and verified copies. Links, junctions and other reparse points are rejected.</summary>
public static class FileInventory
{
    private const string DirectoryMarker = "directory";

    /// <summary>Maps each relative path (empty for the root) to "directory" or the SHA-256 of the file.</summary>
    public static Dictionary<string, string> Build(string path, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(path, string.Empty, result, cancellationToken);
        return result;
    }

    private static void Walk(string path, string prefix, Dictionary<string, string> result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
            throw new StickException(StickErrorCode.UnsupportedFile, "Unsupported file: {0}", info.Name);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new StickException(StickErrorCode.SymbolicLink, "Symbolic links are not supported: {0}. Stick was not cleared.", info.Name);

        if (info is DirectoryInfo directory)
        {
            result[prefix] = DirectoryMarker;
            foreach (var child in directory.EnumerateFileSystemInfos())
                Walk(child.FullName, prefix + "/" + child.Name, result, ct);
            return;
        }
        result[prefix] = Digest(path);
    }

    public static string Digest(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static bool Equal(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);

    /// <summary>Copies a file or directory, then proves source and target are identical and the source did not change.</summary>
    public static void CopyVerified(string source, string target, CancellationToken cancellationToken = default)
    {
        var before = Build(source, cancellationToken);
        Copy(source, target, cancellationToken);
        if (!Equal(Build(target, cancellationToken), before) || !Equal(Build(source, cancellationToken), before))
            throw new StickException(StickErrorCode.VerificationFailed,
                "Verification failed for {0}. Stick was not cleared.", Path.GetFileName(source.TrimEnd('\\', '/')));
    }

    private static void Copy(string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                Copy(entry, Path.Combine(target, Path.GetFileName(entry)), ct);
        }
        else
        {
            File.Copy(source, target, overwrite: false);
        }
    }
}
