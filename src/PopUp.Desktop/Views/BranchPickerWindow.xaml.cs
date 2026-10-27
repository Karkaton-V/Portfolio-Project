using System.Windows;
using System.Windows.Input;
using PopUp.Desktop.ViewModels;

namespace PopUp.Desktop.Views;

public partial class BranchPickerWindow : Window
{
    private readonly BranchPickerViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();

    public BranchPickerWindow(BranchPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await _viewModel.LoadAsync(_closing.Token);
        };
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>The branch to pull once the window closes with OK; null is the default branch.</summary>
    public string? ChosenBranch { get; private set; }

    private void Pull_Click(object sender, RoutedEventArgs e) => Choose();

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => Choose();

    private void Choose()
    {
        if (_viewModel.CanPull)
        {
            ChosenBranch = _viewModel.ChosenBranch;
            DialogResult = true;
        }
    }
}
