using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DumpDebugger_App.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        !(value is bool b && b);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// x:Bind only applies its implicit bool→Visibility conversion when there is no explicit
/// Converter; with a Converter, the compiler trusts its declared return type. Using
/// InverseBoolConverter (returns object boxing a bool) against a Visibility property throws
/// an InvalidCastException at bind time, which crashes WinUI's native host. This converter
/// returns Visibility directly to avoid that.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ErrorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? new SolidColorBrush(Microsoft.UI.Colors.Red)
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ThreadIdsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is IReadOnlyList<int> ids
            ? $"OS thread IDs: {string.Join(", ", ids)}"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
