namespace StickStation.Core.ViewModels;

public interface ILocalizer
{
    /// <summary>Returns the localized string for <paramref name="key"/> (or the key itself when missing).</summary>
    string Get(string key);

    string Format(string key, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}

/// <summary>Everything the view models need from the windowing layer.</summary>
public interface IUserInterface
{
    Task<string?> PickFolderAsync(bool assignment);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive);

    void OpenFolder(string path);
}

public static class ErrorText
{
    /// <summary>Localized description of an error; app-authored errors are looked up by code.</summary>
    public static string Describe(Exception error, ILocalizer localizer)
    {
        if (error is StickException stick && stick.Code != StickErrorCode.Generic)
        {
            var args = stick.Args.Select(a => a is Exception inner ? Describe(inner, localizer) : a).ToArray();
            var key = "Error_" + stick.Code;
            var format = localizer.Get(key);
            if (format != key) return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);
        }
        return error.Message;
    }
}
