using Velopack;

namespace Hotfix;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Must run first: the installer calls the app with hook arguments and expects it to exit.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => TrayContext.Forget())
            .Run();

        using var single = new Mutex(true, @"Local\DenFrieVilje.Hotfix", out bool first);
        if (!first)
        {
            MessageBox.Show("Hotfix is already running. Its icon is in the notification area, by the clock.", "Hotfix");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
