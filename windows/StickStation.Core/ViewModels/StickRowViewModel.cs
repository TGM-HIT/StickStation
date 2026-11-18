using CommunityToolkit.Mvvm.ComponentModel;

namespace StickStation.Core.ViewModels;

public partial class StickRowViewModel : ObservableObject
{
    private readonly ILocalizer localizer;

    public StickRowViewModel(StickVolume volume, ILocalizer localizer)
    {
        Volume = volume;
        this.localizer = localizer;
        StatusText = localizer.Get("Status_ReadyToBackUp");
    }

    public StickVolume Volume { get; }

    public string Id => Volume.Id;
    public string Name => Volume.Name;

    public string CapacityText => FormatBytes(Volume.Capacity) + " · USB";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect), nameof(PhaseTitle), nameof(ShowDisconnected))]
    public partial StickPhase Phase { get; set; } = StickPhase.Todo;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackup))]
    public partial string? BackupPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDisconnected))]
    public partial bool IsConnected { get; set; } = true;

    public bool CanSelect => Phase == StickPhase.Todo;
    public bool HasBackup => BackupPath is not null;
    public bool ShowDisconnected => !IsConnected && Phase != StickPhase.Ejected;

    public string PhaseTitle => localizer.Get("Phase_" + Phase);

    public void Apply(StickUpdate update)
    {
        Phase = update.Phase;
        if (update.Phase == StickPhase.Ejected) IsConnected = false;
        StatusText = update.Error is null
            ? localizer.Get("Status_" + update.Status)
            : ErrorText.Describe(update.Error, localizer);
        if (update.BackupPath is not null) BackupPath = update.BackupPath;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, unit == 0 ? "{0:0} {1}" : "{0:0.#} {1}", value, units[unit]);
    }
}
