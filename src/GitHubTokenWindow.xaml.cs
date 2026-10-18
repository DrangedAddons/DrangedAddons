using System.Diagnostics;
using System.Windows;

namespace DrangedAddons;

/// <summary>Shows how the app is connected to GitHub, explains how to make a free token, and saves / removes one.</summary>
public partial class GitHubTokenWindow : Window
{
    // GitHub's "new fine-grained token" page, pre-filled with a name and description.
    const string NewTokenUrl = "https://github.com/settings/personal-access-tokens/new"
                               + "?name=DrangedAddons&description=Lets%20DrangedAddons%20check%20for%20addon%20updates";

    /// <summary>True when a token was saved or removed, so the caller can recheck things.</summary>
    public bool Changed { get; private set; }

    public GitHubTokenWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        RemoveButton.Visibility = TokenStore.Load() is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) => await ShowStatusAsync();
    }

    async Task ShowStatusAsync()
    {
        SourceText.Text = "Checking…";
        AllowanceText.Text = "";
        var access = await TryGetAccessAsync(null);
        if (access is null)
        {
            SourceText.Text = "Couldn't reach GitHub";
            return;
        }
        SourceText.Text = access.SignedIn ? $"Signed in: {access.Source}" : "Not signed in";
        AllowanceText.Text = $"{access.Remaining:N0} of {access.Limit:N0} checks left this hour (resets at {access.Reset.ToLocalTime():t}).";
    }

    static async Task<GitHubAccess?> TryGetAccessAsync(string? token)
    {
        try
        {
            return await GitHubClient.GetAccessAsync(token);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    void OpenGitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(NewTokenUrl) { UseShellExecute = true });

    void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = TokenBox.Password.Trim().Length > 0;
        MessageText.Visibility = Visibility.Collapsed;
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        SaveButton.IsEnabled = false;
        ShowMessage("Checking the token with GitHub…", "MutedBrush");
        try
        {
            if (await GitHubClient.GetAccessAsync(token) is not { } access)
            {
                ShowMessage("Couldn't reach GitHub to check the token. Try again in a moment.", "DangerBrush");
                return;
            }
            TokenStore.Save(token);
            GitHubClient.UseSavedToken(token);
            Changed = true;
            TokenBox.Clear();
            RemoveButton.Visibility = Visibility.Visible;
            ShowMessage($"Saved. You now have {access.Limit:N0} checks an hour.", "AccentBrush");
            await ShowStatusAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(ex.Message, "DangerBrush");
        }
        finally
        {
            SaveButton.IsEnabled = TokenBox.Password.Trim().Length > 0;
        }
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        TokenStore.Remove();
        GitHubClient.UseSavedToken(null);
        Changed = true;
        RemoveButton.Visibility = Visibility.Collapsed;
        ShowMessage("Removed the saved token.", "MutedBrush");
        await ShowStatusAsync();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ShowMessage(string text, string brushKey)
    {
        MessageText.Text = text;
        MessageText.Foreground = (System.Windows.Media.Brush)FindResource(brushKey);
        MessageText.Visibility = Visibility.Visible;
    }
}
