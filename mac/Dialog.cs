using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DrangedAddons.Mac;

/// <summary>A small message or yes/no window over the main one (Avalonia has no message box of its own).</summary>
static class Dialog
{
    public static Task ShowAsync(Window owner, string title, string message) => Build(owner, title, message, null).ShowDialog(owner);

    /// <summary>True when <paramref name="confirm"/> is clicked.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirm) =>
        await Build(owner, title, message, confirm).ShowDialog<bool?>(owner) == true;

    static Window Build(Window owner, string title, string message, string? confirm)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = owner.Background,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirm is not null)
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "btn" } };
            cancel.Click += (_, _) => window.Close(false);
            var ok = new Button { Content = confirm, IsDefault = true, Classes = { "btn", "accent" } };
            ok.Click += (_, _) => window.Close(true);
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
        }
        else
        {
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Classes = { "btn", "accent" } };
            ok.Click += (_, _) => window.Close();
            buttons.Children.Add(ok);
        }
        window.Content = new StackPanel
        {
            Margin = new Thickness(22, 18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
        return window;
    }
}
