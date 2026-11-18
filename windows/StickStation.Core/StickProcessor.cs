using System.Globalization;

namespace StickStation.Core;

/// <summary>
/// Backs up, resets and ejects one stick. Nothing is deleted until the backup (and the optional
/// assignment staging copy) has been copied and re-verified with SHA-256.
/// </summary>
public sealed class StickProcessor(IVolumeService volumes)
{
    public const string SubmissionFolder = "Abgabe";
    public const string AssignmentFolder = "Angabe";

    private static readonly string[] ProtectedRootNames = ["System Volume Information", "$RECYCLE.BIN"];

    // One ejection at a time, mirroring the macOS app; held across retries.
    private static readonly SemaphoreSlim EjectionQueue = new(1, 1);

    public async Task ProcessAsync(StickVolume volume, ProcessOptions options, Action<StickUpdate> update, CancellationToken cancellationToken = default)
    {
        var effective = options.BackupOnly ? options with { AssignmentSource = null, Format = false } : options;
        try
        {
            await RunAsync(volume, effective, update, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception operationError)
        {
            try
            {
                await OperationLog.AppendAsync(options.BackupRoot, volume.Name, "ERROR: " + operationError.Message).ConfigureAwait(false);
            }
            catch (Exception logError)
            {
                throw new StickException(StickErrorCode.OperationAndLogFailed,
                    "{0} Log could not be written: {1}", operationError, logError);
            }
            throw;
        }
    }

    private static bool IsProtected(string path) =>
        ProtectedRootNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private static List<string> RootEntries(string root) =>
        Directory.GetFileSystemEntries(root).Where(entry => !IsProtected(entry)).ToList();

    private async Task RunAsync(StickVolume volume, ProcessOptions options, Action<StickUpdate> update, CancellationToken ct)
    {
        Task Log(string message) => OperationLog.AppendAsync(options.BackupRoot, volume.Name, message);

        if (!Directory.Exists(options.BackupRoot))
            throw new StickException(StickErrorCode.BackupDirectoryMissing, "The backup directory does not exist.");
        if (options.AssignmentSource is { } missing && !Directory.Exists(missing))
            throw new StickException(StickErrorCode.SourceDirectoryMissing, "The assignment source folder does not exist.");

        var device = volumes.Validate(volume);
        if (options.Format && volumes.MaxFat32Capacity is { } maxFat32 && volume.Capacity > maxFat32)
            throw new StickException(StickErrorCode.Fat32TooLarge,
                "Windows can only format volumes up to 32 GB as FAT32. Use clear-files mode for this stick.");

        // Reject either folder on any managed stick, including other batch members.
        foreach (var folder in new[] { options.BackupRoot, options.AssignmentSource }.OfType<string>())
        {
            if (VolumeLabel.IsValid(volumes.GetVolumeLabel(PathSafety.Resolve(folder))))
                throw new StickException(StickErrorCode.FoldersOnStick, "Backup and source folders must not be on a HIT stick.");
        }
        if (options.AssignmentSource is { } assignment &&
            PathSafety.IsSameOrInside(PathSafety.Resolve(options.BackupRoot), PathSafety.Resolve(assignment)))
            throw new StickException(StickErrorCode.BackupInsideSource, "The backup directory cannot be inside the assignment source.");

        var mode = options.BackupOnly ? "backup only" : options.Format ? "FAT32 format" : "clear files";
        await Log($"Started. Volume: {volume.Id}; mode: {mode}; assignment: {options.AssignmentSource ?? "none"}").ConfigureAwait(false);

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var destination = Path.Combine(options.BackupRoot, $"{volume.Name}_{stamp}_{suffix}");
        Directory.CreateDirectory(destination);
        await Log($"Backup directory created: {destination}").ConfigureAwait(false);
        var payload = Path.Combine(destination, "Files");
        Directory.CreateDirectory(payload);

        var root = volume.RootPath;
        var rootItems = RootEntries(root);
        if (!rootItems.Any(item => Path.GetFileName(item).Equals(SubmissionFolder, StringComparison.OrdinalIgnoreCase)))
            await Log("No Abgabe folder present; continuing with backup and reset").ConfigureAwait(false);

        var snapshots = new List<(string Item, Dictionary<string, string> Snapshot)>();
        foreach (var item in rootItems)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(item);
            if (name.Equals(SubmissionFolder, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(item))
            {
                snapshots.Add((item, FileInventory.Build(item, ct)));
                FileInventory.CopyVerified(item, Path.Combine(payload, name), ct);
                await Log($"Copied and verified: {name}").ConfigureAwait(false);
            }
        }

        // Stage the optional assignment before deleting anything from the stick.
        var staged = Path.Combine(destination, "Assignment");
        if (options.AssignmentSource is { } source)
        {
            FileInventory.CopyVerified(source, staged, ct);
            await Log("Assignment source staged and verified").ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(destination, "Backup.txt"),
            $"Verified backup\nVolume: {volume.Name}\nId: {volume.Id}\nDate: {stamp}\nIncludes Abgabe when present and all root files.\n", ct).ConfigureAwait(false);

        foreach (var (item, snapshot) in snapshots)
        {
            if (!FileInventory.Equal(FileInventory.Build(item, ct), snapshot) ||
                !FileInventory.Equal(FileInventory.Build(Path.Combine(payload, Path.GetFileName(item)), ct), snapshot))
                throw new StickException(StickErrorCode.FilesChangedDuringBackup, "Files changed during backup. No data was deleted.");
        }
        if (!RootEntries(root).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(rootItems))
            throw new StickException(StickErrorCode.RootChangedDuringBackup, "Root directory changed during backup. No data was deleted.");
        await Log("Backup verification completed").ConfigureAwait(false);

        if (options.BackupOnly)
        {
            await Log("Backup-only completed; stick left unchanged and mounted").ConfigureAwait(false);
            update(new(StickPhase.BackedUp, StickStatus.BackupVerifiedUnchanged, destination));
            return;
        }

        update(new(StickPhase.Saved, StickStatus.BackupVerified, destination));
        if (volumes.Validate(volume) != device)
            throw new StickException(StickErrorCode.UsbIdentityChanged, "The USB device changed.");
        update(new(StickPhase.Saved, options.Format ? StickStatus.Formatting : StickStatus.Clearing, destination));

        var current = volume;
        if (options.Format)
        {
            await Log("Starting FAT32 format").ConfigureAwait(false);
            current = await volumes.FormatFat32Async(volume, device, status =>
            {
                update(new(StickPhase.Saved, status, destination));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
            root = current.RootPath;
            await Log("FAT32 format completed").ConfigureAwait(false);
            update(new(StickPhase.Saved, StickStatus.Clearing, destination));
        }
        else
        {
            await Log("Starting removal of all root items").ConfigureAwait(false);
            foreach (var item in Directory.GetFileSystemEntries(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(item);
                if (IsProtected(item))
                {
                    await Log($"Skipped system folder: {name}").ConfigureAwait(false);
                    continue;
                }
                volumes.Validate(volume);
                DeleteEntry(item);
                await Log($"Deleted: {name}").ConfigureAwait(false);
            }
            if (RootEntries(root).Count > 0)
                throw new StickException(StickErrorCode.CleanupIncomplete, "Cleanup is incomplete: items remain on the stick.");
        }

        Directory.CreateDirectory(Path.Combine(root, SubmissionFolder));
        await Log("Created empty Abgabe folder").ConfigureAwait(false);
        var angabe = Path.Combine(root, AssignmentFolder);
        if (options.AssignmentSource is not null)
        {
            FileInventory.CopyVerified(staged, angabe, ct);
            await Log("Created Angabe with verified assignment contents").ConfigureAwait(false);
        }
        else
        {
            Directory.CreateDirectory(angabe);
            await Log("Created empty Angabe folder").ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(destination, "Reset.txt"),
            $"Reset completed: {DateTimeOffset.Now:O}\n", ct).ConfigureAwait(false);
        await Log("Reset completed; starting automatic ejection").ConfigureAwait(false);
        update(new(StickPhase.Reset, StickStatus.ResetEjecting, destination));

        if (volumes.Validate(current) != device)
            throw new StickException(StickErrorCode.IdentityChangedBeforeEject,
                "Reset complete, but device identity changed before ejection.");
        try
        {
            await Log("Waiting for the serial ejection queue").ConfigureAwait(false);
            await EjectionQueue.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await volumes.EjectAsync(current, device, Log, ct).ConfigureAwait(false);
            }
            finally
            {
                EjectionQueue.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StickException(StickErrorCode.ResetButEjectFailed,
                "Backup and reset completed, but ejection failed: {0}", ex);
        }

        update(new(StickPhase.Ejected, StickStatus.SafeToRemove, destination));
        try
        {
            await Log("Ejected successfully; safe to remove").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StickException(StickErrorCode.EjectedButLogFailed,
                "Stick ejected successfully, but the final log entry failed: {0}", ex);
        }
    }

    private static void DeleteEntry(string path)
    {
        try
        {
            DeleteCore(path);
        }
        catch (Exception first) when (first is UnauthorizedAccessException or IOException)
        {
            try
            {
                ClearReadOnly(path);
                DeleteCore(path);
            }
            catch (Exception second) when (second is UnauthorizedAccessException or IOException)
            {
                throw new StickException(StickErrorCode.CleanupFailed,
                    "Cannot delete {0}: {1}", Path.GetFileName(path), second);
            }
        }
    }

    private static void DeleteCore(string path)
    {
        if (Directory.Exists(path))
        {
            // Deleting a junction or symlink removes the link, never its target.
            var isLink = new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
            Directory.Delete(path, recursive: !isLink);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void ClearReadOnly(string path)
    {
        if (Directory.Exists(path))
        {
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) ClearReadOnly(child);
        }
        if (File.Exists(path) || Directory.Exists(path))
            File.SetAttributes(path, File.GetAttributes(path) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
    }
}
