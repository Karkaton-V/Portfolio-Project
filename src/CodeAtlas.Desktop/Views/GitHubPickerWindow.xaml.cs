using System.Windows;
using System.Windows.Input;
using CodeAtlas.Desktop.ViewModels;
using CodeAtlas.Repos;

namespace CodeAtlas.Desktop.Views;

public partial class GitHubPickerWindow : Window
{
    private readonly GitHubPickerViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();

    public GitHubPickerWindow(RepoRegistry registry)
    {
        InitializeComponent();
        _viewModel = new GitHubPickerViewModel(registry);
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await _viewModel.LoadAsync(_closing.Token);
        };
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>The repository the user chose, once the window closes with OK.</summary>
    public GitHubRepoRef? Chosen { get; private set; }

    /// <summary>An existing clone of <see cref="Chosen"/> on this computer, if there is one.</summary>
    public string? ChosenLocalPath { get; private set; }

    private void Add_Click(object sender, RoutedEventArgs e) => Choose();

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => Choose();

    private void Choose()
    {
        if (_viewModel.Selected is { IsAdded: false } item && GitHubRepoRef.TryParse(item.FullName, out var repo))
        {
            Chosen = repo;
            ChosenLocalPath = item.LocalPath;
            DialogResult = true;
        }
    }
}
