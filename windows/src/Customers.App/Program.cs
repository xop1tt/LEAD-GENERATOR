using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Customers.Ui;

/// <summary>Точка входа. Своя (а не созданная XAML), чтобы до запуска интерфейса проверить, что приложение не открыто второй раз.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var options = StartupOptions.Parse(args);
        if (!options.Smoke && !SingleInstance.TryAcquire())
        {
            SingleInstance.NotifyRunningInstance(options.Link);
            return 0;
        }

        Application.Start(callbackParams =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(options);
        });
        return App.ExitCode;
    }
}
