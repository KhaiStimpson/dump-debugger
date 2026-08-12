using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace DumpDebugger_App.Converters;

/// <summary>Maps a Finding.Severity string ("Critical"/"Warning"/"Info"/"Error") to its semantic brush.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Microsoft.UI.Xaml.Application.Current.Resources[BrushKeyFor(value as string)];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static string BrushKeyFor(string? severity) => severity switch
    {
        "Critical" or "Error" => "WorkbenchCriticalBrush",
        "Warning" => "WorkbenchWarningBrush",
        _ => "WorkbenchInfoBrush",
    };
}

public sealed class SeverityToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Microsoft.UI.Xaml.Application.Current.Resources[BrushKeyFor(value as string)];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static string BrushKeyFor(string? severity) => severity switch
    {
        "Critical" or "Error" => "WorkbenchCriticalSoftBrush",
        "Warning" => "WorkbenchWarningSoftBrush",
        _ => "WorkbenchInfoSoftBrush",
    };
}

/// <summary>Visible only when the bound tab index equals the converter parameter (a tab strip's content switch).</summary>
public sealed class TabIndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int current && parameter is string p && int.TryParse(p, out var target) && current == target
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Accent brush when the bound tab index equals the parameter (selected-tab underline/text), else transparent/dim.</summary>
public sealed class TabIndexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var selected = value is int current && parameter is string p && int.TryParse(p, out var target) && current == target;
        return selected
            ? Microsoft.UI.Xaml.Application.Current.Resources["WorkbenchAccentBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Accent brush when selected, a dim (but still visible) ink brush otherwise — used for
/// tab label text, where TabIndexToBrushConverter's Transparent-when-unselected would hide it.</summary>
public sealed class TabIndexToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var selected = value is int current && parameter is string p && int.TryParse(p, out var target) && current == target;
        return selected
            ? Microsoft.UI.Xaml.Application.Current.Resources["WorkbenchAccentBrush"]
            : Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class TabIndexToWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int current && parameter is string p && int.TryParse(p, out var target) && current == target
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
