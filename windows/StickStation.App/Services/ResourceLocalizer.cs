using Microsoft.Windows.ApplicationModel.Resources;
using StickStation.Core.ViewModels;

namespace StickStation.Services;

/// <summary>Looks strings up in the app's .resw files (en-US, de-DE) using the Windows display language.</summary>
public sealed class ResourceLocalizer : ILocalizer
{
    private readonly ResourceLoader loader = new();

    public string Get(string key)
    {
        try
        {
            var value = loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }
}
