using StickStation.Core;
using StickStation.Core.ViewModels;

namespace StickStation.Core.Tests;

public class ViewModelTests
{
    public sealed class Localizer : ILocalizer
    {
        public Dictionary<string, string> Strings { get; } = [];
        public string Get(string key) => Strings.TryGetValue(key, out var value) ? value : key;
    }

    private static MainViewModel Create(Sandbox box, bool confirm = true) =>
        new(box.Volumes, new FakeUi { Confirm = confirm }, new Localizer());

    [Fact]
    public void ReconcileKeepsFinishedRowsAndMarksThemDisconnected()
    {
        using var box = new Sandbox();
        var a = box.AddStick("HIT A01");
        var b = box.AddStick("HIT B01");
        var model = Create(box);

        model.Reconcile(box.Volumes.Discover());
        Assert.Equal(2, model.Sticks.Count);
        Assert.Equal(2, model.SelectedCount);

        var rowA = model.Sticks.Single(s => s.Id == a.Id);
        rowA.Apply(new StickUpdate(StickPhase.Reset, StickStatus.ResetEjecting));
        box.Volumes.Sticks.Remove(a);
        box.Volumes.Sticks.Remove(b);
        model.Reconcile(box.Volumes.Discover());

        var remaining = Assert.Single(model.Sticks);
        Assert.Same(rowA, remaining);
        Assert.False(remaining.IsConnected);

        // A re-inserted stick appears as a fresh to-do row.
        box.Volumes.Sticks.Add(a);
        model.Reconcile(box.Volumes.Discover());
        Assert.Equal(StickPhase.Todo, Assert.Single(model.Sticks).Phase);
    }

    [Fact]
    public async Task BatchRunProcessesSelectedSticksAndReportsFailures()
    {
        using var box = new Sandbox();
        var good = box.AddStick("HIT A01");
        var skipped = box.AddStick("HIT A02");
        var bad = box.AddStick("HIT A03");
        Sandbox.Write(Path.Combine(good.RootPath, "Abgabe", "x.txt"));
        Sandbox.MakeJunction(Path.Combine(bad.RootPath, "Abgabe"), box.Folder("outside"));

        var model = Create(box);
        model.BackupRoot = box.Folder("backups");
        model.Reconcile(box.Volumes.Discover());
        model.Sticks.Single(s => s.Id == skipped.Id).IsSelected = false;
        Assert.True(model.CanStart);

        await model.RunBatchAsync();

        Assert.False(model.Running);
        Assert.Equal(2, model.Completed);
        Assert.Equal(StickPhase.Ejected, model.Sticks.Single(s => s.Id == good.Id).Phase);
        Assert.Equal(StickPhase.Todo, model.Sticks.Single(s => s.Id == skipped.Id).Phase);
        Assert.Equal(StickPhase.Failed, model.Sticks.Single(s => s.Id == bad.Id).Phase);
        Assert.Equal(["HIT A01"], box.Volumes.Ejected);
    }

    [Fact]
    public async Task StartRequiresConfirmation()
    {
        using var box = new Sandbox();
        var stick = box.AddStick();
        var model = Create(box, confirm: false);
        model.BackupRoot = box.Folder("backups");
        model.Reconcile(box.Volumes.Discover());

        await model.StartCommand.ExecuteAsync(null);

        Assert.Equal(StickPhase.Todo, model.Sticks.Single().Phase);
        Assert.Empty(Directory.GetDirectories(model.BackupRoot));
        _ = stick;
    }

    [Fact]
    public void BatchIsLimitedToSixteenSticks()
    {
        using var box = new Sandbox();
        for (var i = 1; i <= 17; i++) box.AddStick($"HIT A{i:00}");
        var model = Create(box);
        model.BackupRoot = box.Folder("backups");
        model.Reconcile(box.Volumes.Discover());

        Assert.Equal(17, model.SelectedCount);
        Assert.False(model.CanStart);
        model.Sticks[0].IsSelected = false;
        Assert.True(model.CanStart);
    }
}
