using System.Windows;

namespace DrangedAddons;

public partial class App : Application
{
    // One running copy per Windows user. Test builds (DRANGEDADDONS_DEV=1) use their own names so they never
    // interfere with the installed app.
    static readonly string InstanceName = @"Local\DrangedAddons" +
        (Environment.GetEnvironmentVariable("DRANGEDADDONS_DEV") == "1" ? "-dev" : "");

    Mutex? _instance;
    bool _ownsInstance;

    /// <summary>True once the app is really quitting (not just hiding to the tray).</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Quits for real: File → Exit, the tray menu, updates and uninstall all go through here.</summary>
    public static void ExitApp()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    void App_Startup(object sender, StartupEventArgs e)
    {
        // Dialogs may show before the main window; closing one mustn't end the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool keepRunning;
        try
        {
            keepRunning = AppInstaller.HandleStartup(e.Args);
        }
        catch (Exception ex)
        {
            DarkDialog.Show(null, "Couldn't install DrangedAddons", $"{ex.Message}\n\nIt will run from where it is for now.");
            keepRunning = true;
        }

        if (!keepRunning || !ClaimInstance(replace: e.Args.Contains("--replace")))
        {
            Shutdown();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    /// <summary>
    /// Makes this the only running copy. If another copy is running, it's asked to show its window and this one
    /// exits; with --replace (after an update) the old copy is asked to quit instead, and this one takes over.
    /// </summary>
    bool ClaimInstance(bool replace)
    {
        _instance = new Mutex(false, InstanceName + ".Instance");
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Quit");

        _ownsInstance = TryTake(TimeSpan.Zero);
        if (!_ownsInstance && replace)
        {
            quit.Set();
            _ownsInstance = TryTake(TimeSpan.FromSeconds(20));
        }
        if (!_ownsInstance)
        {
            show.Set();
            return false;
        }

        // Listen for later launches (show the window) and for a newer copy taking over (quit).
        var listener = new Thread(() =>
        {
            while (true)
            {
                var which = WaitHandle.WaitAny(new WaitHandle[] { show, quit });
                Dispatcher.BeginInvoke(() =>
                {
                    if (which == 0) (MainWindow as MainWindow)?.ShowFromTray();
                    else ExitApp();
                });
                if (which == 1) return;
            }
        }) { IsBackground = true, Name = "Single instance" };
        listener.Start();
        return true;
    }

    bool TryTake(TimeSpan timeout)
    {
        try
        {
            return _instance!.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous copy ended without releasing it
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true; // signing out / shutting down: don't hide to the tray
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance) _instance?.ReleaseMutex();
        base.OnExit(e);
    }
}
