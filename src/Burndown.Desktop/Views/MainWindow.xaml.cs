using System.IO;
using System.Windows;
using Burndown.Desktop.ViewModels;
using Burndown.Repos;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace Burndown.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(RepoRegistry registry)
    {
        InitializeComponent();

        // Open at a comfortable size that fits the screen: a fixed size can be taller than the work area
        // on smaller or highly scaled displays, which pushes the window under the taskbar.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1440, workArea.Width * 0.9);
        Height = Math.Min(900, workArea.Height * 0.9);

        // WebView2 keeps its browser profile (cache, the report's saved theme) in a folder it can write to.
        // The default is next to the .exe, which isn't writable once the app is installed somewhere like
        // Program Files, so use the user's local app data. This must be set before the viewer starts.
        Viewer.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Burndown", "WebView2"),
        };
        Viewer.CoreWebView2InitializationCompleted += OnViewerInitialized;

        _viewModel = new MainViewModel(registry);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ReportUri))
            {
                ShowReport();
            }
        };
        DataContext = _viewModel;
        ShowReport();
    }

    /// <summary>Navigates the viewer to the current report. Setting Source also starts WebView2 on first use.</summary>
    private void ShowReport()
    {
        if (_viewModel.ReportUri is { } uri && Viewer.Source != uri)
        {
            Viewer.Source = uri;
        }
    }

    private void OnViewerInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            MessageBox.Show(this,
                "The report viewer needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11. " +
                "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and restart Burndown.\n\n" +
                e.InitializationException?.Message,
                "Burndown", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The report is a local file; links it contains (e.g. to GitHub) open in the user's browser instead.
        Viewer.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri) { UseShellExecute = true });
        };
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder that contains a C# solution" };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.AddFolderAsync(dialog.FolderName);
        }
    }

    private async void AddFromGitHub_Click(object sender, RoutedEventArgs e)
    {
        var picker = new GitHubPickerWindow(_viewModel.Registry) { Owner = this };
        if (picker.ShowDialog() == true && picker.Chosen is { } chosen)
        {
            await _viewModel.AddFromGitHubAsync(chosen, picker.ChosenLocalPath);
        }
    }

    /// <summary>Points the selected repository at the folder where it lives on this computer.</summary>
    private async void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRepo is not { } repo)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = $"Where is {repo.Detail} on this computer?" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // A folder that's a clone of a different repository is probably a mistake; ask before using it.
        var origin = LocalRepoFinder.ReadOrigin(dialog.FolderName);
        if (repo.Repo.GitHub is { } expected
            && !string.Equals(origin?.FullName, expected, StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show(this,
                $"That folder is {(origin is null ? "not a clone of a GitHub repository" : $"a clone of {origin.FullName}")}, " +
                $"not {expected}. Use it for {repo.Name} anyway?",
                "Locate repository", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.LocateAsync(repo, dialog.FolderName);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRepo is not { } repo)
        {
            return;
        }

        if (repo.Repo.Managed)
        {
            var answer = MessageBox.Show(this,
                $"Remove {repo.Name} from Burndown?\n\nBurndown downloaded this repository to {repo.Repo.Path}. " +
                "Choose Yes to delete that copy too, or No to keep the files.",
                "Remove repository", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.Cancel)
            {
                _viewModel.Remove(repo, deleteClone: answer == MessageBoxResult.Yes);
            }
        }
        else if (MessageBox.Show(this,
                     $"Remove {repo.Name} from Burndown? Your files at {repo.Repo.Path} aren't touched.",
                     "Remove repository", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            _viewModel.Remove(repo, deleteClone: false);
        }
    }
}
