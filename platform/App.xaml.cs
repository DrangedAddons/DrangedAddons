using System.Windows;
using DrangedAddons;

namespace DrangedPlatform;

public partial class App : Application
{
    const string InstanceName = @"Local\DrangedPlatform";
    Mutex? _instance;

    void App_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool keepRunning;
        try
        {
            keepRunning = Installer.HandleStartup(e.Args);
        }
        catch (Exception ex)
        {
            DarkDialog.Show(null, "Couldn't install DrangedAddons Platform", $"{ex.Message}\n\nIt will run from where it is for now.");
            keepRunning = true;
        }
        if (!keepRunning || !ClaimInstance(e.Args.Contains("--replace")))
        {
            Shutdown();
            return;
        }
        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    /// <summary>One copy at a time: a second launch brings the first to the front; --replace (an upgrade) takes over.</summary>
    bool ClaimInstance(bool replace)
    {
        _instance = new Mutex(false, InstanceName + ".Instance");
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Quit");
        var owned = Take(TimeSpan.Zero);
        if (!owned && replace)
        {
            quit.Set();
            owned = Take(TimeSpan.FromSeconds(20));
        }
        if (!owned)
        {
            show.Set();
            return false;
        }
        new Thread(() =>
        {
            var handles = new WaitHandle[] { show, quit };
            while (true)
            {
                var which = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(() =>
                {
                    if (which == 1) { Shutdown(); return; }
                    if (MainWindow is { } w)
                    {
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate();
                    }
                });
                if (which == 1) return;
            }
        }) { IsBackground = true }.Start();
        return true;
    }

    bool Take(TimeSpan wait)
    {
        try { return _instance!.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }
}
