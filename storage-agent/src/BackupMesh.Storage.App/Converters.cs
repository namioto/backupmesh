using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BackupMesh.Storage.App;

// Shows an empty-state hint overlaid on a grid/list when its bound collection has no items yet.
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Hides a secondary explanatory line (e.g. the Backups grid's Last backup issue reason) when there is
// nothing to say, rather than showing an empty row that still reserves layout space.
public sealed class StringEmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Hides a detail line that only makes sense once something is selected (e.g. the Source Agents tab's
// certificate summary line, shown per SelectedSourceAgent).
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
