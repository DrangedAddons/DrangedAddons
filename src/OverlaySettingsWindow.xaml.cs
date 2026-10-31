using System.Windows;
using System.Windows.Input;

namespace DrangedAddons;

/// <summary>This player's overlay settings for one game: whether it shows, and the key that opens it.</summary>
public partial class OverlaySettingsWindow : Window
{
    bool _waiting;

    public bool On { get; private set; }
    /// <summary>As text, e.g. "Shift+Tab".</summary>
    public string Hotkey { get; private set; }

    public OverlaySettingsWindow(string game, bool on, string? hotkey)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        On = on;
        Hotkey = OverlayHotkey.Parse(hotkey) is not null ? hotkey! : OverlayHotkey.Default;
        AboutText.Text = $"Your lobby, friends and lobby chat, over {game} while you play.";
        OnCheck.Content = $"Show the overlay in {game}";
        OnCheck.IsChecked = on;
        ShowKey();
    }

    void ShowKey()
    {
        KeyText.Text = _waiting ? "Press a key…" : Hotkey;
        KeyText.Foreground = (System.Windows.Media.Brush)FindResource(_waiting ? "MutedBrush" : "TextBrush");
        KeyBox.BorderBrush = (System.Windows.Media.Brush)FindResource(_waiting ? "AccentBrush" : "LineBrush");
        ChangeButton.Content = _waiting ? "Stop" : "Change";
        ResetButton.IsEnabled = Hotkey != OverlayHotkey.Default;
        KeyRow.IsEnabled = OnCheck.IsChecked == true;
    }

    void On_Click(object sender, RoutedEventArgs e)
    {
        _waiting = false;
        ShowKey();
    }

    void Change_Click(object sender, RoutedEventArgs e)
    {
        _waiting = !_waiting;
        ProblemText.Visibility = Visibility.Collapsed;
        ShowKey();
        if (_waiting) KeyBox.Focus();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _waiting = false;
        Hotkey = OverlayHotkey.Default;
        ProblemText.Visibility = Visibility.Collapsed;
        ShowKey();
    }

    void KeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_waiting || ChangeButton.IsKeyboardFocusWithin) return;
        _waiting = false;
        ShowKey();
    }

    void KeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_waiting) return;
        e.Handled = true; // Tab, Esc and Alt mean the key being set here, not moving focus or closing
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var modifiers = Keyboard.Modifiers;
        if (OverlayHotkey.Problem(modifiers, key) is { } problem)
        {
            ProblemText.Text = $"{OverlayHotkey.Format(modifiers, key)}: {problem}";
            ProblemText.Visibility = Visibility.Visible;
            return;
        }
        ProblemText.Visibility = Visibility.Collapsed;
        Hotkey = OverlayHotkey.Format(modifiers, key);
        _waiting = false;
        ShowKey();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        On = OnCheck.IsChecked == true;
        DialogResult = true;
    }
}
