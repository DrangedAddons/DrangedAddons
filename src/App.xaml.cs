using System.Windows;

namespace DrangedAddons;

public partial class App : Application
{
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

        if (!keepRunning)
        {
            Shutdown();
            return;
        }
        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
