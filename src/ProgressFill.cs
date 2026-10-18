using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>
/// Attached ProgressFill.Value (0–1, or null) for buttons using ProgressButtonTemplate: how far the gold fill
/// reaches. The pacing (quick but never ahead of the real download) is done by <see cref="ProgressPacer"/>.
/// </summary>
public static class ProgressFill
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double?), typeof(ProgressFill), new PropertyMetadata(null, OnValueChanged));

    public static double? GetValue(DependencyObject element) => (double?)element.GetValue(ValueProperty);
    public static void SetValue(DependencyObject element, double? value) => element.SetValue(ValueProperty, value);

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button) return;
        button.ApplyTemplate();
        if (button.Template?.FindName("FillScale", button) is ScaleTransform scale)
        {
            scale.ScaleX = Math.Clamp((double?)e.NewValue ?? 0, 0, 1);
        }
        else
        {
            // Template not applied yet (a row created mid-download): catch up once it is.
            button.Loaded -= ApplyOnLoad;
            button.Loaded += ApplyOnLoad;
        }
    }

    static void ApplyOnLoad(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.Loaded -= ApplyOnLoad;
        if (button.Template?.FindName("FillScale", button) is ScaleTransform scale)
            scale.ScaleX = Math.Clamp(GetValue(button) ?? 0, 0, 1);
    }
}
