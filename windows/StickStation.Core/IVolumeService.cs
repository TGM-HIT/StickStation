namespace StickStation.Core;

/// <summary>Platform operations on removable volumes. Implemented for Windows; faked in tests.</summary>
public interface IVolumeService
{
    /// <summary>Largest volume that can be formatted as FAT32 on this platform, or null when unlimited.</summary>
    long? MaxFat32Capacity { get; }

    /// <summary>Mounted external USB volumes whose label matches a managed HIT stick.</summary>
    IReadOnlyList<StickVolume> Discover();

    /// <summary>Re-checks that the volume is still the same external USB partition and returns a device identity.</summary>
    string Validate(StickVolume volume);

    /// <summary>Formats the volume as FAT32 (keeping its label) and returns the refreshed volume.</summary>
    Task<StickVolume> FormatFat32Async(StickVolume volume, string device, Func<StickStatus, Task> reportStatus, CancellationToken cancellationToken);

    /// <summary>Safely ejects the device and returns once the volume has disappeared.</summary>
    Task EjectAsync(StickVolume volume, string device, Func<string, Task> log, CancellationToken cancellationToken);

    /// <summary>Label of the volume hosting <paramref name="path"/>, or null when unknown (for example network shares).</summary>
    string? GetVolumeLabel(string path);
}
