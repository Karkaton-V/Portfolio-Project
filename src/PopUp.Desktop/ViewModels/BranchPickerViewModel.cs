using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PopUp.Desktop.ViewModels;

/// <summary>One branch in the Pull Xtra picker.</summary>
public sealed class BranchItem(string name, bool isDefault, bool isCurrent)
{
    public string Name { get; } = name;

    public bool IsDefault { get; } = isDefault;

    /// <summary>"default", "analyzed", both, or nothing.</summary>
    public string Tags { get; } = string.Join(" · ", new[] { isDefault ? "default" : null, isCurrent ? "analyzed" : null }.OfType<string>());

    public bool HasTags => Tags.Length > 0;
}

/// <summary>
/// Pull Xtra: lists a GitHub repository's branches (default first, then the one last analyzed, then the rest by
/// name) so one can be picked, pulled, and analyzed.
/// </summary>
public sealed partial class BranchPickerViewModel(
    string repositoryName,
    bool analyzed,
    string? analyzedBranch,
    Func<CancellationToken, Task<(IReadOnlyList<string> Branches, string DefaultBranch)>> load) : ObservableObject
{
    private IReadOnlyList<BranchItem> _all = [];

    public string Heading => $"Pull a branch of {repositoryName}";

    public ObservableCollection<BranchItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPull))]
    private BranchItem? _selected;

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPull))]
    private bool _isLoading;

    /// <summary>Loading, errors, and "nothing matches", shown over the list.</summary>
    [ObservableProperty]
    private string _message = "";

    public bool CanPull => Selected is not null && !IsLoading;

    /// <summary>The branch to pull, or null for the default branch (which Pull Latest also uses).</summary>
    public string? ChosenBranch => Selected is { IsDefault: true } ? null : Selected?.Name;

    partial void OnSearchChanged(string value) => Refresh();

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        Message = "Getting the branches from GitHub…";
        try
        {
            var (branches, defaultBranch) = await load(cancellationToken);
            // The branch last analyzed: the one recorded, or the default (none recorded) once it has been analyzed at all.
            var current = analyzed ? analyzedBranch ?? defaultBranch : null;
            _all = branches
                .OrderByDescending(name => name == defaultBranch)
                .ThenByDescending(name => name == current)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new BranchItem(name, name == defaultBranch, name == current))
                .ToList();
            Message = "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        Refresh();
    }

    private void Refresh()
    {
        var shown = _all.Where(b => b.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        Items.Clear();
        foreach (var branch in shown)
        {
            Items.Add(branch);
        }

        Selected ??= Items.FirstOrDefault(b => b.Tags.Contains("analyzed", StringComparison.Ordinal)) ?? Items.FirstOrDefault();
        if (!IsLoading && _all.Count > 0)
        {
            Message = Items.Count == 0 ? $"No branch matches \"{Search.Trim()}\"." : "";
        }
    }
}
