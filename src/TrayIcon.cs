using System.Drawing;
using Forms = System.Windows.Forms;

namespace DrangedAddons;

/// <summary>The notification-area (system tray) icon: click to open, right-click for a small dark menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    Action? _balloonClicked;

    public TrayIcon(Action open, Action checkForUpdates, Action exit)
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()),
            BackColor = Color.FromArgb(0x1F, 0x23, 0x2B),
            ForeColor = Color.FromArgb(0xE6, 0xE8, 0xEC),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9f),
        };
        var openItem = new Forms.ToolStripMenuItem("Open DrangedAddons", null, (_, _) => open()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Check for updates", null, (_, _) => checkForUpdates()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit()));
        foreach (Forms.ToolStripItem item in menu.Items)
            item.ForeColor = menu.ForeColor;

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DrangedAddons",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
        _icon.BalloonTipClicked += (_, _) => (_balloonClicked ?? open)();
    }

    /// <summary>Hover text (Windows limits it to 127 characters).</summary>
    public string Tooltip
    {
        set => _icon.Text = value.Length > 127 ? value[..127] : value;
    }

    /// <summary>A Windows notification from the tray icon. Clicking it runs <paramref name="clicked"/> (default: open the window).</summary>
    public void Notify(string title, string text, Action? clicked = null)
    {
        _balloonClicked = clicked;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    static Icon LoadIcon()
    {
        // The app icon, at the size Windows uses for tray icons (it holds hand-tuned small frames).
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
        return stream is null ? SystemIcons.Application : new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        static readonly Color Panel = Color.FromArgb(0x1F, 0x23, 0x2B);
        static readonly Color Hover = Color.FromArgb(0x28, 0x2D, 0x37);
        static readonly Color Line = Color.FromArgb(0x32, 0x38, 0x44);

        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Panel;
    }
}
