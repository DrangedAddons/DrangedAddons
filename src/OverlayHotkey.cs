using System.Windows.Input;

namespace DrangedAddons;

/// <summary>
/// The key that opens the in-game overlay, kept as text like "Shift+Tab" (modifiers in the order Ctrl, Alt, Shift,
/// then the key). Each game has its own; a player can change it.
/// </summary>
public static class OverlayHotkey
{
    public const string Default = "Shift+Tab";

    /// <summary>The key without modifiers is fine on its own: one nobody types with.</summary>
    static bool StandsAlone(Key key) =>
        key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Name(key));
        return string.Join("+", parts);
    }

    static string Name(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Oem3 => "`",
        Key.Scroll => "ScrollLock",
        _ => key.ToString(),
    };

    public static (ModifierKeys Modifiers, Key Key)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var modifiers = ModifierKeys.None;
        var parts = text.Split('+');
        foreach (var part in parts[..^1])
        {
            switch (part)
            {
                case "Ctrl": modifiers |= ModifierKeys.Control; break;
                case "Alt": modifiers |= ModifierKeys.Alt; break;
                case "Shift": modifiers |= ModifierKeys.Shift; break;
                default: return null;
            }
        }
        var name = parts[^1];
        Key key;
        if (name.Length == 1 && char.IsDigit(name[0])) key = Key.D0 + (name[0] - '0');
        else if (name == "Enter") key = Key.Return;
        else if (name == "Backspace") key = Key.Back;
        else if (name == "`") key = Key.Oem3;
        else if (name == "ScrollLock") key = Key.Scroll;
        else if (!Enum.TryParse(name, out key)) return null;
        return (modifiers, key);
    }

    /// <summary>Why a key can't open the overlay, or null if it can.</summary>
    public static string? Problem(ModifierKeys modifiers, Key key)
    {
        if (key == Key.Escape) return "Esc closes the overlay, so it can't open it too.";
        if (key is Key.LWin or Key.RWin || modifiers.HasFlag(ModifierKeys.Windows)) return "Windows keys belong to Windows.";
        if (modifiers == ModifierKeys.Alt && key is Key.Tab or Key.F4 or Key.Escape) return $"Alt+{key} belongs to Windows.";
        if ((modifiers & ~ModifierKeys.Shift) == ModifierKeys.None && !StandsAlone(key) && !(modifiers == ModifierKeys.Shift && key == Key.Tab))
            return "Add Ctrl or Alt, or use a key like F1–F12, so it doesn't get in the way of typing.";
        return null;
    }
}
