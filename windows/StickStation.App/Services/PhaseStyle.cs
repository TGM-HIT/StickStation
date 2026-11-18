using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StickStation.Core;

namespace StickStation.Services;

/// <summary>Glyphs and brushes per stick phase, used by x:Bind function bindings.</summary>
public static class PhaseStyle
{
    public static string Glyph(StickPhase phase) => phase switch
    {
        StickPhase.Working or StickPhase.Saved => "\uE895",
        StickPhase.Reset or StickPhase.BackedUp => "\uE73E",
        StickPhase.Ejected => "\uE7C8",
        StickPhase.Failed => "\uE783",
        _ => "\uE823",
    };

    public static Brush Brush(StickPhase phase) => (Brush)Application.Current.Resources[phase switch
    {
        StickPhase.Working or StickPhase.Saved => "SystemFillColorAttentionBrush",
        StickPhase.Reset or StickPhase.BackedUp or StickPhase.Ejected => "SystemFillColorSuccessBrush",
        StickPhase.Failed => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    }];
}
