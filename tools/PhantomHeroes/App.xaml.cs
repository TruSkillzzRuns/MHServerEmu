using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PhantomHeroes;

public partial class App : Application
{
    public App()
    {
        // Any WPF exception on startup that we don't catch would result in the
        // app "silently doing nothing" from the user's POV. Catch them and
        // surface via message box + crash log so users can actually see what
        // went wrong instead of staring at an unmoving desktop.
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowAndLog(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) ShowAndLog(ex);
    }

    private static void ShowAndLog(Exception ex)
    {
        string body = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "PhantomHeroes-crash.log");
            File.WriteAllText(logPath, DateTime.Now + "\n" + body);
        }
        catch { /* best-effort */ }
        try
        {
            MessageBox.Show(body, "Phantom Heroes — startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* if even MessageBox is dead we can't help */ }
    }
}
