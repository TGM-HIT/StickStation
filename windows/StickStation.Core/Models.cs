using System.Text.RegularExpressions;

namespace StickStation.Core;

/// <summary>A mounted USB volume that looks like a managed HIT stick.</summary>
/// <param name="Id">Stable volume identity (the Windows volume GUID path).</param>
/// <param name="Name">Volume label, for example "HIT A01".</param>
/// <param name="RootPath">Mount point, for example "E:\".</param>
/// <param name="DiskNumber">Physical disk number, when known.</param>
public sealed record StickVolume(string Id, string Name, string RootPath, long Capacity, int? DiskNumber = null);

public enum StickPhase { Todo, Working, Saved, Reset, Ejected, BackedUp, Failed }

public enum StickStatus
{
    ReadyToBackUp,
    CopyingAndVerifying,
    BackupVerified,
    BackupVerifiedUnchanged,
    Formatting,
    Clearing,
    AdministratorPermissionRequired,
    ResetEjecting,
    SafeToRemove,
    Failed,
}

/// <summary>Progress notification from a stick worker. <see cref="Error"/> is set for <see cref="StickStatus.Failed"/>.</summary>
public sealed record StickUpdate(StickPhase Phase, StickStatus Status, string? BackupPath = null, Exception? Error = null);

public sealed record ProcessOptions(string BackupRoot, string? AssignmentSource, bool Format, bool BackupOnly);

public static class VolumeLabel
{
    private static readonly Regex Pattern = new(@"^HIT [ABCD](0[1-9]|[1-3][0-9]|40)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValid(string? name) => name is not null && Pattern.IsMatch(name);
}

public enum StickErrorCode
{
    Generic,
    SymbolicLink,
    UnsupportedFile,
    VerificationFailed,
    FilesChangedDuringBackup,
    RootChangedDuringBackup,
    FoldersOnStick,
    BackupInsideSource,
    BackupDirectoryMissing,
    SourceDirectoryMissing,
    LogCreateFailed,
    UsbIdentityChanged,
    DeviceChangedBeforeEject,
    DiskOperationFailed,
    AdministratorCancelled,
    FormatNotRemounted,
    Fat32TooLarge,
    CleanupIncomplete,
    CleanupFailed,
    EjectFailed,
    ResetButEjectFailed,
    EjectedButLogFailed,
    OperationAndLogFailed,
    IdentityChangedBeforeEject,
}

/// <summary>
/// App-authored error. <see cref="Exception.Message"/> is always English (used for the log);
/// the UI localizes it from <see cref="Code"/> and <see cref="Args"/>.
/// </summary>
public sealed class StickException : Exception
{
    public StickErrorCode Code { get; }
    public IReadOnlyList<object?> Args { get; }

    public StickException(StickErrorCode code, string message, params object?[] args)
        : base(string.Format(System.Globalization.CultureInfo.InvariantCulture, message,
            args.Select(a => a is StickException s ? s.Message : a is Exception e ? e.Message : a).ToArray()))
    {
        Code = code;
        Args = args;
    }
}
