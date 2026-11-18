using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace StickStation.Core.Windows;

/// <summary>Windows implementation: WMI storage classes for identity, an elevated Format-Volume for FAT32, and the shell "Eject" verb.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsVolumeService : IVolumeService
{
    private const ushort BusTypeUsb = 7;
    private const int ErrorCancelled = 1223;
    private const long Fat32Limit = 32L * 1024 * 1024 * 1024;

    // Each format needs its own UAC prompt; show them one at a time.
    private static readonly SemaphoreSlim FormatQueue = new(1, 1);

    public long? MaxFat32Capacity => Fat32Limit;

    private sealed record DiskInfo(ushort BusType, string UniqueId, bool IsSystem, bool IsBoot);

    public IReadOnlyList<StickVolume> Discover()
    {
        var candidates = new List<(DriveInfo Drive, string Label)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed) || !drive.IsReady) continue;
                var label = drive.VolumeLabel;
                if (VolumeLabel.IsValid(label)) candidates.Add((drive, label));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (candidates.Count == 0) return [];

        var partitions = QueryPartitions();
        var disks = QueryDisks();
        var result = new List<StickVolume>();
        foreach (var (drive, label) in candidates)
        {
            var letter = char.ToUpperInvariant(drive.Name[0]);
            if (!partitions.TryGetValue(letter, out var diskNumber) ||
                !disks.TryGetValue(diskNumber, out var disk) ||
                disk.BusType != BusTypeUsb || disk.IsSystem || disk.IsBoot) continue;
            var guid = GetVolumeGuid(drive.Name);
            if (guid is null) continue;
            result.Add(new StickVolume(guid, label, drive.Name, drive.TotalSize, (int)diskNumber));
        }
        return result.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
    }

    public string Validate(StickVolume volume)
    {
        var match = Discover().FirstOrDefault(v =>
            string.Equals(v.RootPath, volume.RootPath, StringComparison.OrdinalIgnoreCase));
        if (match is null || match.Name != volume.Name || match.Id != volume.Id || match.DiskNumber != volume.DiskNumber ||
            match.DiskNumber is not { } number || !QueryDisks().TryGetValue((uint)number, out var disk))
            throw new StickException(StickErrorCode.UsbIdentityChanged,
                "USB identity changed or this is not a supported USB partition. Reconnect and refresh.");
        return $"disk{number}:{disk.UniqueId}";
    }

    public async Task<StickVolume> FormatFat32Async(StickVolume volume, string device, Func<StickStatus, Task> reportStatus, CancellationToken cancellationToken)
    {
        if (!VolumeLabel.IsValid(volume.Name) || volume.DiskNumber is not { } disk ||
            !GuidPathPattern().IsMatch(volume.Id) || !DriveRootPattern().IsMatch(volume.RootPath))
            throw new StickException(StickErrorCode.UsbIdentityChanged,
                "USB identity changed or this is not a supported USB partition. Reconnect and refresh.");

        await FormatQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await reportStatus(StickStatus.AdministratorPermissionRequired).ConfigureAwait(false);
            await RunElevatedFormatAsync(volume, disk, cancellationToken).ConfigureAwait(false);
            await reportStatus(StickStatus.Formatting).ConfigureAwait(false);
        }
        finally
        {
            FormatQueue.Release();
        }

        // The drive letter normally survives a format; wait for the volume to come back on the same disk.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refreshed = Discover().FirstOrDefault(v => v.DiskNumber == volume.DiskNumber && v.Name == volume.Name);
            if (refreshed is not null && Validate(refreshed) == device) return refreshed;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new StickException(StickErrorCode.FormatNotRemounted, "Formatted, but the volume did not remount. Backup is safe.");
    }

    private static async Task RunElevatedFormatAsync(StickVolume volume, int disk, CancellationToken ct)
    {
        var letter = volume.RootPath[0];
        var errorFile = Path.Combine(Path.GetTempPath(), $"StickStation-format-{Guid.NewGuid():N}.txt");
        // Every interpolated value was validated by the caller. The elevated script re-checks the
        // volume identity because the UAC prompt gives the user time to swap sticks.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
              $v = Get-Volume -DriveLetter '{{letter}}'
              if ($v.UniqueId -ne '{{volume.Id}}') { exit 3 }
              $p = Get-Partition -DriveLetter '{{letter}}'
              if ($p.DiskNumber -ne {{disk}}) { exit 3 }
              $d = Get-Disk -Number {{disk}}
              if ($d.BusType -ne 'USB' -or $d.IsSystem -or $d.IsBoot) { exit 3 }
              Format-Volume -DriveLetter '{{letter}}' -FileSystem FAT32 -NewFileSystemLabel '{{volume.Name}}' -Force -Confirm:$false | Out-Null
            } catch {
              [System.IO.File]::WriteAllText('{{errorFile}}', $_.Exception.Message)
              exit 1
            }
            """;
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
        };
        try
        {
            using var process = Process.Start(startInfo) ?? throw new StickException(StickErrorCode.AdministratorCancelled,
                "Administrator permission was cancelled; the stick was not formatted.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            switch (process.ExitCode)
            {
                case 0: return;
                case 3:
                    throw new StickException(StickErrorCode.UsbIdentityChanged,
                        "USB identity changed or this is not a supported USB partition. Reconnect and refresh.");
                default:
                    var detail = File.Exists(errorFile) ? File.ReadAllText(errorFile).Trim() : $"exit code {process.ExitCode}";
                    throw new StickException(StickErrorCode.DiskOperationFailed, "Disk operation failed: {0}", detail);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new StickException(StickErrorCode.AdministratorCancelled,
                "Administrator permission was cancelled; the stick was not formatted.");
        }
        finally
        {
            try { File.Delete(errorFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public async Task EjectAsync(StickVolume volume, string device, Func<string, Task> log, CancellationToken cancellationToken)
    {
        // All app-owned file handles have been closed before the ejection queue is entered.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        const int attempts = 5;
        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!VolumeStillPresent(volume)) return;
            if (Validate(volume) != device)
                throw new StickException(StickErrorCode.DeviceChangedBeforeEject, "The USB device changed before ejection.");
            await log($"Ejection attempt {attempt + 1} of {attempts}").ConfigureAwait(false);
            try
            {
                await RunOnStaThread(() => InvokeShellEject(volume.RootPath)).ConfigureAwait(false);
                for (var wait = 0; wait < 20; wait++)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    if (!VolumeStillPresent(volume)) return;
                }
                last = new IOException("The volume is still mounted; another process may be using it.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
            await log($"Ejection failed: {last.Message}").ConfigureAwait(false);
            if (attempt < attempts - 1) await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }
        throw new StickException(StickErrorCode.EjectFailed, "Ejection failed: {0}", last!);
    }

    private static bool VolumeStillPresent(StickVolume volume)
    {
        try
        {
            return new DriveInfo(volume.RootPath).IsReady && GetVolumeGuid(volume.RootPath) == volume.Id;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void InvokeShellEject(string rootPath)
    {
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application is unavailable.");
        object shell = Activator.CreateInstance(type)!;
        object? folder = null, item = null;
        try
        {
            folder = ((dynamic)shell).NameSpace(17); // ssfDRIVES ("This PC")
            item = ((dynamic)folder!).ParseName(rootPath.TrimEnd('\\'));
            if (item is null) throw new IOException("The drive is no longer available.");
            ((dynamic)item).InvokeVerb("Eject");
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(shell);
        }
    }

    private static Task RunOnStaThread(Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public string? GetVolumeLabel(string path)
    {
        var root = new StringBuilder(1024);
        if (!GetVolumePathNameW(Path.GetFullPath(path), root, root.Capacity)) return null;
        var label = new StringBuilder(261);
        return GetVolumeInformationW(root.ToString(), label, label.Capacity, out _, out _, out _, null, 0)
            ? label.ToString() : null;
    }

    private static string? GetVolumeGuid(string root)
    {
        var builder = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPointW(root, builder, builder.Capacity) ? builder.ToString() : null;
    }

    private static Dictionary<char, uint> QueryPartitions()
    {
        var result = new Dictionary<char, uint>();
        using var searcher = new ManagementObjectSearcher(StorageScope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition"));
        foreach (ManagementBaseObject partition in searcher.Get())
        {
            if (partition["DriveLetter"] is { } letter && partition["DiskNumber"] is { } number)
            {
                var c = Convert.ToChar(letter);
                if (c != '\0') result[char.ToUpperInvariant(c)] = Convert.ToUInt32(number);
            }
            partition.Dispose();
        }
        return result;
    }

    private static Dictionary<uint, DiskInfo> QueryDisks()
    {
        var result = new Dictionary<uint, DiskInfo>();
        using var searcher = new ManagementObjectSearcher(StorageScope, new ObjectQuery("SELECT Number, BusType, UniqueId, IsSystem, IsBoot FROM MSFT_Disk"));
        foreach (ManagementBaseObject disk in searcher.Get())
        {
            result[Convert.ToUInt32(disk["Number"])] = new DiskInfo(
                Convert.ToUInt16(disk["BusType"]),
                Convert.ToString(disk["UniqueId"])?.Trim() ?? string.Empty,
                disk["IsSystem"] is true, disk["IsBoot"] is true);
            disk.Dispose();
        }
        return result;
    }

    private static ManagementScope StorageScope => new(@"\\.\ROOT\Microsoft\Windows\Storage");

    [GeneratedRegex(@"^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\$")]
    private static partial Regex GuidPathPattern();

    [GeneratedRegex(@"^[A-Za-z]:\\$")]
    private static partial Regex DriveRootPattern();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPath, StringBuilder volumeName, int volumeNameSize,
        out uint serial, out uint maxComponentLength, out uint flags, StringBuilder? fileSystemName, int fileSystemNameSize);
}
