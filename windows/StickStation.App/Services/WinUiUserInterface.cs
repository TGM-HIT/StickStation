using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using StickStation.Core.ViewModels;

namespace StickStation.Services;

public sealed class WinUiUserInterface(ILocalizer localizer) : IUserInterface
{
    public async Task<string?> PickFolderAsync(bool assignment)
    {
        var picker = new FolderPicker(App.Window.AppWindow.Id)
        {
            CommitButtonText = localizer.Get("ChooseFolder"),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = App.Window.Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = localizer.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (destructive) dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }
}
