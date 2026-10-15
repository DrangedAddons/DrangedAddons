using System.Windows;

namespace DrangedAddons;

public partial class App : Application
{
    void App_Startup(object sender, StartupEventArgs e)
    {
        bool keepRunning;
        try
        {
            keepRunning = AppInstaller.HandleStartup(e.Args);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't install DrangedAddons:\n\n{ex.Message}\n\nIt will run from where it is for now.",
                "DrangedAddons", MessageBoxButton.OK, MessageBoxImage.Warning);
            keepRunning = true;
        }

        if (!keepRunning)
        {
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}
