using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StickStation.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public const int MaxBatchSize = 16;

    private readonly IVolumeService volumes;
    private readonly IUserInterface ui;
    private readonly ILocalizer localizer;
    private readonly StickProcessor processor;
    private readonly HashSet<string> activeIds = [];
    private readonly object completionLock = new();
    private int refreshGeneration;

    public MainViewModel(IVolumeService volumes, IUserInterface ui, ILocalizer localizer)
    {
        this.volumes = volumes;
        this.ui = ui;
        this.localizer = localizer;
        processor = new StickProcessor(volumes);
    }

    public ObservableCollection<StickRowViewModel> Sticks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupFolderName), nameof(HasBackupRoot), nameof(FooterHint))]
    public partial string? BackupRoot { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceFolderName), nameof(HasSource))]
    public partial string? SourceRoot { get; set; }

    [ObservableProperty]
    public partial bool Format { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAssignment), nameof(CanEditFormat), nameof(ActionText), nameof(LimitText), nameof(HasSource))]
    public partial bool BackupOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanEditAssignment), nameof(CanEditFormat), nameof(HeaderStatusText), nameof(FinishedText))]
    public partial bool Running { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FinishedText))]
    public partial int Completed { get; set; }

    public bool IsIdle => !Running;
    public bool CanEditAssignment => !Running && !BackupOnly;
    public bool CanEditFormat => !Running && !BackupOnly;
    public bool HasBackupRoot => BackupRoot is not null;
    public bool HasSource => SourceRoot is not null && !BackupOnly;
    public bool IsEmpty => Sticks.Count == 0;
    public bool HasSticks => Sticks.Count > 0;

    public string BackupFolderName => BackupRoot is null ? localizer.Get("ChooseFolderPlaceholder") : FolderName(BackupRoot);
    public string SourceFolderName => SourceRoot is null ? localizer.Get("ChooseFolderPlaceholder") : FolderName(SourceRoot);

    public int SelectedCount => Sticks.Count(s => s.IsSelected && s.Phase == StickPhase.Todo);
    public int TodoCount => Sticks.Count(s => s.Phase == StickPhase.Todo);
    public int InProcessCount => Sticks.Count(s => s.Phase is StickPhase.Working or StickPhase.Saved);
    public int CompletedCount => Sticks.Count(s => s.Phase is StickPhase.Reset or StickPhase.Ejected or StickPhase.BackedUp);

    public string SelectedText => localizer.Format(SelectedCount == 1 ? "SticksSelected_One" : "SticksSelected_Other", SelectedCount);
    public string FinishedText => localizer.Format("Finished", Completed);
    public string HeaderStatusText => localizer.Get(Running ? "Header_Processing" : "Header_Monitoring");
    public string ActionText => localizer.Get(BackupOnly ? "Action_BackUpOnly" : "Action_BackUpReset");
    public string LimitText => localizer.Get(BackupOnly ? "Limit_BackupOnly" : "Limit_Eject");
    public string FooterHint => localizer.Get(BackupRoot is null ? "Footer_ChooseBackup" : "Footer_Verified");

    public bool CanStart => !Running && BackupRoot is not null && SelectedCount is > 0 and <= MaxBatchSize;

    [RelayCommand]
    private async Task ChooseBackupFolderAsync()
    {
        if (await ui.PickFolderAsync(assignment: false) is { } path) BackupRoot = path;
    }

    [RelayCommand]
    private async Task ChooseAssignmentFolderAsync()
    {
        if (await ui.PickFolderAsync(assignment: true) is { } path) SourceRoot = path;
    }

    [RelayCommand]
    private void ClearAssignment() => SourceRoot = null;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Sticks.Where(s => s.Phase == StickPhase.Todo)) row.IsSelected = true;
    }

    [RelayCommand]
    private void OpenBackup(StickRowViewModel? row)
    {
        if (row?.BackupPath is { } path) ui.OpenFolder(path);
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync();

    public async Task RefreshCoreAsync()
    {
        var generation = ++refreshGeneration;
        IReadOnlyList<StickVolume> found;
        try
        {
            found = await Task.Run(volumes.Discover);
        }
        catch (Exception)
        {
            return; // Keep the current list; the next poll retries.
        }
        if (generation == refreshGeneration) Reconcile(found);
    }

    public async Task RunMonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        await RefreshCoreAsync();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshCoreAsync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Keeps rows of sticks that are being processed stable and remembers finished ones.</summary>
    public void Reconcile(IReadOnlyList<StickVolume> found)
    {
        var remaining = found.ToList();
        var updated = new List<StickRowViewModel>();
        foreach (var row in Sticks)
        {
            if (activeIds.Contains(row.Id))
            {
                remaining.RemoveAll(v => v.Id == row.Id || string.Equals(v.RootPath, row.Volume.RootPath, StringComparison.OrdinalIgnoreCase));
                updated.Add(row);
            }
            else if (remaining.FindIndex(v => v.Id == row.Id) is var index and >= 0)
            {
                var volume = remaining[index];
                remaining.RemoveAt(index);
                if (!row.IsConnected)
                {
                    updated.Add(CreateRow(volume));
                }
                else
                {
                    row.IsConnected = true;
                    updated.Add(row);
                }
            }
            else if (row.Phase != StickPhase.Todo)
            {
                row.IsConnected = false;
                updated.Add(row);
            }
        }
        updated.AddRange(remaining.Select(CreateRow));
        updated.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (!updated.SequenceEqual(Sticks))
        {
            Sticks.Clear();
            foreach (var row in updated) Sticks.Add(row);
        }
        Recalculate();
    }

    private StickRowViewModel CreateRow(StickVolume volume)
    {
        var row = new StickRowViewModel(volume, localizer);
        row.PropertyChanged += OnRowChanged;
        return row;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StickRowViewModel.IsSelected) or nameof(StickRowViewModel.Phase)) Recalculate();
    }

    private void Recalculate()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(TodoCount));
        OnPropertyChanged(nameof(InProcessCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSticks));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnRunningChanged(bool value) => Recalculate();
    partial void OnBackupRootChanged(string? value) => Recalculate();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart) return;
        var count = SelectedCount;
        var one = count == 1;
        var title = localizer.Format(BackupOnly ? (one ? "Confirm_BackupTitle_One" : "Confirm_BackupTitle_Other")
                                                : (one ? "Confirm_ResetTitle_One" : "Confirm_ResetTitle_Other"), count);
        var message = localizer.Get(BackupOnly ? "Confirm_BackupOnly" : Format ? "Confirm_Format" : "Confirm_Clear");
        var button = localizer.Get(BackupOnly ? "Action_BackUpOnly" : Format ? "Action_BackUpFormat" : "Action_BackUpReset");
        if (!await ui.ConfirmAsync(title, message, button, destructive: !BackupOnly)) return;
        await RunBatchAsync();
    }

    /// <summary>Processes up to 16 selected sticks in parallel and returns when all have finished.</summary>
    public async Task RunBatchAsync()
    {
        if (Running || BackupRoot is null) return;
        var batch = Sticks.Where(s => s.IsSelected && s.Phase == StickPhase.Todo).Take(MaxBatchSize).ToList();
        if (batch.Count == 0) return;

        var options = new ProcessOptions(BackupRoot, BackupOnly ? null : SourceRoot, !BackupOnly && Format, BackupOnly);
        activeIds.Clear();
        foreach (var row in batch) activeIds.Add(row.Id);
        Running = true;
        Completed = 0;
        foreach (var row in batch)
        {
            row.BackupPath = null;
            row.Apply(new StickUpdate(StickPhase.Working, StickStatus.CopyingAndVerifying));
        }

        var context = SynchronizationContext.Current;
        try
        {
            await Task.WhenAll(batch.Select(row => ProcessOneAsync(row, options, context)));
        }
        finally
        {
            activeIds.Clear();
            Running = false;
        }
        await RefreshCoreAsync();
    }

    private async Task ProcessOneAsync(StickRowViewModel row, ProcessOptions options, SynchronizationContext? context)
    {
        void Post(StickUpdate update)
        {
            if (context is null) row.Apply(update);
            else context.Post(_ => row.Apply(update), null);
        }

        try
        {
            await Task.Run(() => processor.ProcessAsync(row.Volume, options, Post));
        }
        catch (Exception ex)
        {
            Post(new StickUpdate(StickPhase.Failed, StickStatus.Failed, null, ex));
        }
        lock (completionLock) Completed++;
    }

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
