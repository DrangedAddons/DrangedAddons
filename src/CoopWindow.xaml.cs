using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>
/// Hosts or joins Azeroth Survivors co-op over a direct, hole-punched connection (<see cref="CoopTunnel"/>) instead
/// of the game's relay, which sits overseas: two players in the same country saw 500+ ms through it. The window stays
/// open while the game runs, because the connection lives in this app; closing it, or the game, ends co-op.
/// </summary>
public partial class CoopWindow : Window
{
    readonly string _exe;
    CoopHost? _host;
    CoopJoin? _join;
    Process? _game;
    bool _starting;

    public CoopWindow(string exe)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _exe = exe;
        UpdateMode();
        if (!CoopTunnel.IsSupported)
        {
            AddStatus("Direct co-op needs Windows 11 or Windows Server 2022 (for QUIC); this PC doesn't have it.", danger: true);
            StartButton.IsEnabled = false;
        }
    }

    bool Hosting => HostTab.IsChecked == true;
    bool Active => _host is not null || _join is not null;

    void Mode_Checked(object sender, RoutedEventArgs e) => UpdateMode();

    void UpdateMode()
    {
        if (HostPanel is null || JoinPanel is null || StartButton is null) return; // still in InitializeComponent
        HostPanel.Visibility = Hosting ? Visibility.Visible : Visibility.Collapsed;
        JoinPanel.Visibility = Hosting ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = Hosting ? "Host and start" : "Join and start";
        StatusList.Children.Clear();
        if (!Hosting) CodeBox.Focus();
    }

    void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StatusList is not null && !Active) StatusList.Children.Clear();
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || Active) return;
        string? code = null;
        if (!Hosting && (code = CoopTunnel.NormalizeCode(CodeBox.Text)) is null)
        {
            StatusList.Children.Clear();
            AddStatus("A lobby code is 6 letters and numbers, like K7MPQ2.", danger: true);
            return;
        }

        _starting = true;
        StartButton.IsEnabled = false;
        ModeTabs.IsEnabled = PasswordPanel.IsEnabled = CodeBox.IsEnabled = false;
        StatusList.Children.Clear();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Dictionary<string, string> env;
            if (Hosting)
            {
                AddStatus("Opening a lobby…");
                var gamePort = CoopTunnel.FreeLocalPort();
                _host = await CoopHost.StartAsync(gamePort, cancel.Token);
                _host.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line));
                CodeText.Text = _host.Code;
                CodePanel.Visibility = Visibility.Visible;
                HostHint.Visibility = Visibility.Collapsed;
                env = AzerothSurvivorsGame.DirectCoopEnvironment(gamePort, null, PasswordBox.Password);
                AddStatus("Lobby open. Send the others the code.");
            }
            else
            {
                _join = await CoopJoin.ConnectAsync(code!, line => Dispatcher.BeginInvoke(() => AddStatus(line)), cancel.Token);
                _join.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line, danger: true));
                env = AzerothSurvivorsGame.DirectCoopEnvironment(null, $"127.0.0.1:{_join.LocalPort}", PasswordBox.Password);
                AddStatus("Connected to the host directly.");
            }

            StartGame(env);
            AddStatus(Hosting ? "Started the game. Enter the zone once everyone has joined." : "Started the game: it joins the host's party.");
            AddStatus("Keep this window open while you play: the connection runs through it.");
            CloseButton.Content = "End co-op";
        }
        catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException
                                       or OperationCanceledException or System.ComponentModel.Win32Exception or System.Net.Quic.QuicException)
        {
            AddStatus(ex switch
            {
                OperationCanceledException => "Timed out.",
                System.Net.WebSockets.WebSocketException => "Couldn't reach the co-op rendezvous. Check your internet connection.",
                System.ComponentModel.Win32Exception => "Couldn't start the game: " + ex.Message,
                _ => char.ToUpper(ex.Message[0]) + ex.Message[1..] + ".",
            }, danger: true);
            await EndAsync();
            StartButton.IsEnabled = true;
            ModeTabs.IsEnabled = PasswordPanel.IsEnabled = CodeBox.IsEnabled = true;
        }
        finally
        {
            _starting = false;
        }
    }

    void StartGame(Dictionary<string, string> env)
    {
        var info = new ProcessStartInfo(_exe)
        {
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
            UseShellExecute = false, // needed to hand the game its own environment variables
        };
        foreach (var (name, value) in env) info.Environment[name] = value;
        _game = Process.Start(info) ?? throw new System.ComponentModel.Win32Exception("Windows didn't start it");
        _game.EnableRaisingEvents = true;
        _game.Exited += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            if (!Active) return;
            AddStatus("The game closed, so co-op ended.");
            await EndAsync();
            CloseButton.Content = "Close";
        });
    }

    async Task EndAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        if (_join is not null) await _join.DisposeAsync();
        _host = null;
        _join = null;
        _game?.Dispose();
        _game = null;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeText.Text);
            CopyButton.Content = "Copied";
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!Active || _game is not { HasExited: false }) { await EndAsync(); return; }
        if (!DarkDialog.Show(this, "End co-op?",
                Hosting ? "The others will lose their connection to your game." : "Your game will lose its connection to the host.",
                "End co-op", "Keep playing"))
        {
            e.Cancel = true;
            return;
        }
        await EndAsync();
    }

    void AddStatus(string text, bool danger = false) =>
        StatusList.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = (Brush)FindResource(danger ? "DangerBrush" : "MutedBrush"),
        });
}
