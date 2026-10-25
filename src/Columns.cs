using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace DrangedAddons;

/// <summary>An optional column in the addon list (Name and Action are always there). Order and visibility are the user's.</summary>
public sealed class ListColumn(string key, string header, double width) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string Header { get; } = header;
    public double Width { get; } = width;

    bool _isDragging;
    /// <summary>Its header is being dragged to a new position (highlighted).</summary>
    public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value, nameof(IsDragging)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set(ref bool field, bool value, string name)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// A list cell's text: values are the column key, the row, and any row property (only there so the cell refreshes
/// when the row raises a change).
/// </summary>
public sealed class CellTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [string key, AddonRow row, ..] ? row.CellText(key) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
