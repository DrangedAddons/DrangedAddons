using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DrangedAddons;

/// <summary>Edits the local account: picture, name and status. The caller saves what's chosen.</summary>
public partial class AccountWindow : Window
{
    public sealed record StatusItem(AccountStatus Status, string Label, Brush Brush);

    /// <summary>The name typed; null when left empty (the default name is used).</summary>
    public string? ChosenName { get; private set; }
    public AccountStatus ChosenStatus { get; private set; }

    /// <summary>Whether the picture was replaced or removed; <see cref="ChosenPicture"/> is the new one (null: removed).</summary>
    public bool PictureChanged { get; private set; }
    public byte[]? ChosenPicture { get; private set; }

    readonly bool _welcome;

    /// <param name="defaultName">The name used when none is typed (the hero last played, or the Windows user name).</param>
    /// <param name="welcome">First run: asks for a name (filled in with the default) and a picture, without status.</param>
    public AccountWindow(AccountSettings account, string defaultName, ImageSource? picture, bool welcome = false)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _welcome = welcome;
        NameBox.Text = welcome ? account.Name ?? defaultName : account.Name ?? "";
        if (welcome)
        {
            Title = "Welcome";
            HeadingText.Text = "Welcome to DrangedAddons";
            WelcomeText.Visibility = Visibility.Visible;
            StatusPanel.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            SaveButton.Content = "Continue";
        }
        NamePlaceholder.Text = defaultName;
        StatusList.ItemsSource = new[]
        {
            new StatusItem(AccountStatus.Online, "Online", Account.StateBrush("online")),
            new StatusItem(AccountStatus.Away, "Away", Account.StateBrush("away")),
            new StatusItem(AccountStatus.AppearOffline, "Appear offline", Account.StateBrush("offline")),
        };
        StatusList.SelectedIndex = (int)account.Status;
        ShowPicture(picture);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    void ShowPicture(ImageSource? picture)
    {
        PictureBrush.ImageSource = picture;
        PictureCircle.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
        InitialsText.Visibility = picture is null ? Visibility.Visible : Visibility.Collapsed;
        RemovePictureButton.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateInitials();
    }

    void UpdateInitials()
    {
        var name = NameBox.Text.Trim();
        InitialsText.Text = Account.Initials(name.Length > 0 ? name : NamePlaceholder.Text);
    }

    void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        NamePlaceholder.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_welcome) SaveButton.IsEnabled = NameBox.Text.Trim().Length > 0; // the welcome asks for a name
        UpdateInitials();
    }

    void ChoosePicture_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = Account.MakePicture(dialog.FileName);
            ChosenPicture = bytes;
            PictureChanged = true;
            PictureError.Visibility = Visibility.Collapsed;
            ShowPicture(Account.Decode(bytes));
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            PictureError.Text = "That file isn't a picture this PC can open.";
            PictureError.Visibility = Visibility.Visible;
        }
    }

    void RemovePicture_Click(object sender, RoutedEventArgs e)
    {
        ChosenPicture = null;
        PictureChanged = true;
        ShowPicture(null);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        ChosenName = NameBox.Text.Trim() is { Length: > 0 } name ? name : null;
        ChosenStatus = (StatusList.SelectedItem as StatusItem)?.Status ?? AccountStatus.Online;
        DialogResult = true;
    }
}
