using StickStation.Core;
using StickStation.Core.ViewModels;

namespace StickStation.Core.Tests;

/// <summary>Temp-directory sandbox: "sticks" are plain folders, so no test ever touches a real drive.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "StickStationTests-" + Guid.NewGuid().ToString("N"));
    public FakeVolumes Volumes { get; }

    public Sandbox()
    {
        Directory.CreateDirectory(Root);
        Volumes = new FakeVolumes();
    }

    public string Folder(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public StickVolume AddStick(string name = "HIT A01", long capacity = 8_000_000_000)
    {
        var root = Folder("stick-" + name.Replace(' ', '_'));
        var volume = new StickVolume("vol-" + name, name, root, capacity, 1);
        Volumes.Sticks.Add(volume);
        return volume;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void Write(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static void MakeJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

public sealed class FakeVolumes : IVolumeService
{
    public List<StickVolume> Sticks { get; } = [];
    public List<string> Ejected { get; } = [];
    public List<string> Formatted { get; } = [];
    public bool FailEjection { get; set; }
    public long? MaxFat32Capacity { get; set; } = 32_000_000_000;

    public IReadOnlyList<StickVolume> Discover() => Sticks.ToList();

    public string Validate(StickVolume volume) => "device-" + volume.Name;

    public Task<StickVolume> FormatFat32Async(StickVolume volume, string device, Func<StickStatus, Task> reportStatus, CancellationToken ct)
    {
        Formatted.Add(volume.Name);
        foreach (var entry in Directory.GetFileSystemEntries(volume.RootPath))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
        }
        return Task.FromResult(volume);
    }

    public Task EjectAsync(StickVolume volume, string device, Func<string, Task> log, CancellationToken ct)
    {
        if (FailEjection) throw new IOException("busy");
        Ejected.Add(volume.Name);
        Sticks.RemoveAll(s => s.Id == volume.Id);
        return Task.CompletedTask;
    }

    public string? GetVolumeLabel(string path)
    {
        var full = Path.GetFullPath(path);
        return Sticks.FirstOrDefault(s => PathSafety.IsSameOrInside(full, s.RootPath))?.Name;
    }
}

public sealed class FakeUi : IUserInterface
{
    public bool Confirm { get; set; } = true;
    public Task<string?> PickFolderAsync(bool assignment) => Task.FromResult<string?>(null);
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive) => Task.FromResult(Confirm);
    public void OpenFolder(string path) { }
}
