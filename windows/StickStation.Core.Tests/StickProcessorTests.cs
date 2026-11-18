using StickStation.Core;

namespace StickStation.Core.Tests;

public class StickProcessorTests
{
    private static async Task<List<StickUpdate>> Run(Sandbox box, StickVolume stick, ProcessOptions options)
    {
        var updates = new List<StickUpdate>();
        await new StickProcessor(box.Volumes).ProcessAsync(stick, options, updates.Add);
        return updates;
    }

    private static Sandbox PreparedStick(out StickVolume stick)
    {
        var box = new Sandbox();
        stick = box.AddStick();
        Sandbox.Write(Path.Combine(stick.RootPath, "Abgabe", "work.txt"), "submission");
        Sandbox.Write(Path.Combine(stick.RootPath, "Abgabe", "deep", "more.txt"), "deeper");
        Sandbox.Write(Path.Combine(stick.RootPath, "readme.txt"), "root file");
        Sandbox.Write(Path.Combine(stick.RootPath, "Other", "ignored.txt"), "not backed up");
        return box;
    }

    [Fact]
    public async Task BacksUpResetsAndEjects()
    {
        using var box = PreparedStick(out var stick);
        var backups = box.Folder("backups");
        var assignment = box.Folder("assignment");
        Sandbox.Write(Path.Combine(assignment, "task.txt"), "do this");

        var updates = await Run(box, stick, new(backups, assignment, Format: false, BackupOnly: false));

        var run = Assert.Single(Directory.GetDirectories(backups));
        Assert.StartsWith("HIT A01_", Path.GetFileName(run));
        Assert.Equal("submission", File.ReadAllText(Path.Combine(run, "Files", "Abgabe", "work.txt")));
        Assert.Equal("root file", File.ReadAllText(Path.Combine(run, "Files", "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(run, "Files", "Other")));
        Assert.Equal("do this", File.ReadAllText(Path.Combine(run, "Assignment", "task.txt")));
        Assert.True(File.Exists(Path.Combine(run, "Backup.txt")));
        Assert.True(File.Exists(Path.Combine(run, "Reset.txt")));

        Assert.Equal(["Abgabe", "Angabe"], Directory.GetFileSystemEntries(stick.RootPath).Select(p => Path.GetFileName(p)!).Order().ToArray());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(stick.RootPath, "Abgabe")));
        Assert.Equal("do this", File.ReadAllText(Path.Combine(stick.RootPath, "Angabe", "task.txt")));

        Assert.Equal(["HIT A01"], box.Volumes.Ejected);
        Assert.Equal(StickPhase.Ejected, updates[^1].Phase);
        Assert.Equal(StickStatus.SafeToRemove, updates[^1].Status);
        var log = File.ReadAllText(Path.Combine(backups, "StickStation.log"));
        Assert.Contains("[HIT A01]", log);
        Assert.Contains("Ejected successfully", log);
    }

    [Fact]
    public async Task ReadOnlyFilesAreRemovedWhenClearing()
    {
        using var box = PreparedStick(out var stick);
        var locked = Path.Combine(stick.RootPath, "Other", "ignored.txt");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        await Run(box, stick, new(box.Folder("backups"), null, false, false));

        Assert.False(Directory.Exists(Path.Combine(stick.RootPath, "Other")));
        Assert.True(Directory.Exists(Path.Combine(stick.RootPath, "Angabe")));
    }

    [Fact]
    public async Task EmptyStickGetsFreshFolders()
    {
        using var box = new Sandbox();
        var stick = box.AddStick("HIT D40");
        await Run(box, stick, new(box.Folder("backups"), null, false, false));
        Assert.True(Directory.Exists(Path.Combine(stick.RootPath, "Abgabe")));
        Assert.True(Directory.Exists(Path.Combine(stick.RootPath, "Angabe")));
    }

    [Fact]
    public async Task BackupOnlyLeavesStickUntouched()
    {
        using var box = PreparedStick(out var stick);
        var assignment = box.Folder("assignment");
        Sandbox.Write(Path.Combine(assignment, "task.txt"));

        var updates = await Run(box, stick, new(box.Folder("backups"), assignment, Format: true, BackupOnly: true));

        Assert.True(File.Exists(Path.Combine(stick.RootPath, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(stick.RootPath, "Abgabe", "work.txt")));
        Assert.False(Directory.Exists(Path.Combine(stick.RootPath, "Angabe")));
        Assert.Empty(box.Volumes.Ejected);
        Assert.Empty(box.Volumes.Formatted);
        Assert.Equal(StickPhase.BackedUp, updates[^1].Phase);
    }

    [Fact]
    public async Task FormatModeFormatsAndRecreatesFolders()
    {
        using var box = PreparedStick(out var stick);
        await Run(box, stick, new(box.Folder("backups"), null, Format: true, BackupOnly: false));
        Assert.Equal(["HIT A01"], box.Volumes.Formatted);
        Assert.Equal(["Abgabe", "Angabe"], Directory.GetFileSystemEntries(stick.RootPath).Select(p => Path.GetFileName(p)!).Order().ToArray());
    }

    [Fact]
    public async Task OversizedStickIsRejectedBeforeAnythingHappens()
    {
        using var box = new Sandbox();
        var stick = box.AddStick(capacity: 64_000_000_000);
        var backups = box.Folder("backups");
        var error = await Assert.ThrowsAsync<StickException>(() => Run(box, stick, new(backups, null, true, false)));
        Assert.Equal(StickErrorCode.Fat32TooLarge, error.Code);
        Assert.Empty(Directory.GetDirectories(backups));
    }

    [Fact]
    public async Task LinksInAbgabeStopBeforeAnythingIsDeleted()
    {
        using var box = PreparedStick(out var stick);
        Sandbox.MakeJunction(Path.Combine(stick.RootPath, "Abgabe", "escape"), box.Folder("outside"));

        var error = await Assert.ThrowsAsync<StickException>(() => Run(box, stick, new(box.Folder("backups"), null, false, false)));

        Assert.Equal(StickErrorCode.SymbolicLink, error.Code);
        Assert.True(File.Exists(Path.Combine(stick.RootPath, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(stick.RootPath, "Abgabe", "work.txt")));
        Assert.Empty(box.Volumes.Ejected);
    }

    [Fact]
    public async Task BackupDirectoryOnManagedStickIsRejected()
    {
        using var box = PreparedStick(out var stick);
        var other = box.AddStick("HIT B02");
        var backupsOnOtherStick = Path.Combine(other.RootPath, "backups");
        Directory.CreateDirectory(backupsOnOtherStick);

        var error = await Assert.ThrowsAsync<StickException>(() => Run(box, stick, new(backupsOnOtherStick, null, false, false)));

        Assert.Equal(StickErrorCode.FoldersOnStick, error.Code);
        Assert.True(File.Exists(Path.Combine(stick.RootPath, "readme.txt")));
    }

    [Fact]
    public async Task AssignmentSourceOnManagedStickIsRejected()
    {
        using var box = PreparedStick(out var stick);
        var other = box.AddStick("HIT B02");
        var error = await Assert.ThrowsAsync<StickException>(() => Run(box, stick, new(box.Folder("backups"), other.RootPath, false, false)));
        Assert.Equal(StickErrorCode.FoldersOnStick, error.Code);
    }

    [Fact]
    public async Task BackupDirectoryInsideAssignmentSourceIsRejected()
    {
        using var box = PreparedStick(out var stick);
        var assignment = box.Folder("assignment");
        var backups = Path.Combine(assignment, "backups");
        Directory.CreateDirectory(backups);

        var error = await Assert.ThrowsAsync<StickException>(() => Run(box, stick, new(backups, assignment, false, false)));

        Assert.Equal(StickErrorCode.BackupInsideSource, error.Code);
        Assert.True(File.Exists(Path.Combine(stick.RootPath, "readme.txt")));
    }

    [Fact]
    public async Task BackupDirectoryReachedThroughJunctionIntoSourceIsRejected()
    {
        using var box = PreparedStick(out var stick);
        var assignment = box.Folder("assignment");
        var backups = box.Folder("backups");
        var viaLink = Path.Combine(box.Root, "link");
        Sandbox.MakeJunction(viaLink, assignment);
        Directory.CreateDirectory(Path.Combine(assignment, "inner"));

        var error = await Assert.ThrowsAsync<StickException>(() =>
            Run(box, stick, new(Path.Combine(viaLink, "inner"), assignment, false, false)));
        Assert.Equal(StickErrorCode.BackupInsideSource, error.Code);
        _ = backups;
    }

    [Fact]
    public async Task EjectionFailureIsReportedAfterBackupAndReset()
    {
        using var box = PreparedStick(out var stick);
        box.Volumes.FailEjection = true;
        var backups = box.Folder("backups");

        var error = await Assert.ThrowsAsync<StickException>(() => Run(box, stick, new(backups, null, false, false)));

        Assert.Equal(StickErrorCode.ResetButEjectFailed, error.Code);
        Assert.Contains("ERROR", File.ReadAllText(Path.Combine(backups, "StickStation.log")));
        Assert.True(File.Exists(Directory.GetDirectories(backups).Single() + "\\Reset.txt"));
    }

    [Fact]
    public async Task EachRunGetsItsOwnBackupFolder()
    {
        using var box = PreparedStick(out var stick);
        var backups = box.Folder("backups");
        await Run(box, stick, new(backups, null, false, true));
        await Run(box, stick, new(backups, null, false, true));
        Assert.Equal(2, Directory.GetDirectories(backups).Length);
    }

    [Fact]
    public void ErrorsAreLocalizedByCode()
    {
        var localizer = new ViewModelTests.Localizer();
        localizer.Strings["Error_SymbolicLink"] = "Symbolische Links: {0}";
        var error = new StickException(StickErrorCode.SymbolicLink, "Symbolic links are not supported: {0}.", "x");
        Assert.Equal("Symbolische Links: x", ViewModels.ErrorText.Describe(error, localizer));
        Assert.Equal("boom", ViewModels.ErrorText.Describe(new IOException("boom"), localizer));
    }
}
