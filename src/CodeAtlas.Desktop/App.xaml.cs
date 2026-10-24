using System.Windows;
using System.Windows.Threading;
using CodeAtlas.Desktop.Views;
using CodeAtlas.Repos;

namespace CodeAtlas.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Anything unexpected is shown to the user instead of silently closing the window.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        if (LoadRegistry() is { } registry)
        {
            new MainWindow(registry).Show();
        }
        else
        {
            Shutdown();
        }
    }

    /// <summary>
    /// Loads the saved repositories. If the file can't be read (say it was edited by hand), offers to set it
    /// aside and start with an empty list rather than refusing to open.
    /// </summary>
    private static RepoRegistry? LoadRegistry()
    {
        try
        {
            return RepoRegistry.Load();
        }
        catch (RepoRegistryException ex)
        {
            var answer = MessageBox.Show(
                $"CodeAtlas couldn't read its list of saved repositories.\n\n{ex.Message}\n\n" +
                "Choose OK to keep a copy of that file and start with an empty list, or Cancel to quit and fix it yourself.",
                "CodeAtlas", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                return null;
            }

            RepoRegistry.SetAsideDamaged();
            return RepoRegistry.Load();
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Errors raised while a window is being created arrive wrapped ("The invocation of the constructor…");
        // the innermost exception is the one that explains what went wrong.
        MessageBox.Show(e.Exception.GetBaseException().Message, "CodeAtlas ran into a problem", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
