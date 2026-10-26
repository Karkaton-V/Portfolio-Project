using System.Collections.ObjectModel;
using System.Net.Http;
using PopUp.Repos;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PopUp.Desktop.ViewModels;

/// <summary>One of the user's GitHub repositories in the picker.</summary>
/// <param name="localPath">An existing clone on this computer (e.g. from GitHub Desktop), if one was found.</param>
public sealed class GitHubRepoItem(GitHubRepo repo, bool isAdded, string? localPath)
{
    public GitHubRepo Repo { get; } = repo;

    public bool IsAdded { get; } = isAdded;

    public string? LocalPath { get; } = localPath;

    public string FullName => Repo.FullName;

    public string Description => Repo.Description ?? "";

    public string Language => Repo.Language ?? "—";

    public string Visibility => Repo.Private ? "Private" : "Public";

    public string Updated => Repo.PushedAt is { } at ? Format.Relative(at) : "";

    public string Status => IsAdded ? "Added" : LocalPath is not null ? "On this PC" : "";

    public string StatusTip => LocalPath is not null ? $"Uses your copy at {LocalPath}" : "Downloads a copy";
}

/// <summary>
/// Lists every repository the signed-in GitHub account owns, collaborates on, or can see through an
/// organization, with search and a C#-only filter.
/// </summary>
public sealed partial class GitHubPickerViewModel(RepoRegistry registry) : ObservableObject
{
    private static readonly HttpClient Http = new() { BaseAddress = GitHubClient.ApiBase };

    private IReadOnlyList<GitHubRepoItem> _all = [];

    public ObservableCollection<GitHubRepoItem> Items { get; } = [];

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>Shown instead of the list when there's nothing to list (not signed in, an error, no matches).</summary>
    [ObservableProperty]
    private string _message = "Loading your repositories…";

    [ObservableProperty]
    private string _account = "";

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private bool _cSharpOnly = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private GitHubRepoItem? _selected;

    public bool CanAdd => Selected is { IsAdded: false };

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnCSharpOnlyChanged(bool value) => ApplyFilter();

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await Tools.FindGitHubTokenAsync(cancellationToken);
            if (token is null)
            {
                Message = "PopUp uses your GitHub CLI sign-in. Install the GitHub CLI (cli.github.com) and run " +
                    "`gh auth login`, or set a GITHUB_TOKEN environment variable, then open this window again.";
                return;
            }

            var client = new GitHubClient(Http, token);
            Account = $"Signed in as {await client.GetLoginAsync(cancellationToken)}";
            var added = registry.Repos.Select(r => r.GitHub).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Look for existing clones (GitHub Desktop's and Visual Studio's folders) while GitHub answers.
            var local = Task.Run(() => LocalRepoFinder.Find(), cancellationToken);
            var repos = await client.ListMyRepositoriesAsync(cancellationToken);
            var clones = await local;

            _all = repos
                .Where(r => !r.Archived)
                .Select(r => new GitHubRepoItem(r, added.Contains(r.FullName), clones.GetValueOrDefault(r.FullName)?.FirstOrDefault()))
                .ToList();
            ApplyFilter();
        }
        catch (GitHubException ex)
        {
            Message = ex.Message;
        }
        catch (HttpRequestException)
        {
            Message = "Couldn't reach GitHub. Check your internet connection and try again.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var search = Search.Trim();
        var matches = _all
            .Where(r => !CSharpOnly || r.Repo.IsCSharp)
            .Where(r => search.Length == 0
                || r.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Items.Clear();
        foreach (var item in matches)
        {
            Items.Add(item);
        }

        Message = matches.Count > 0 ? ""
            : _all.Count == 0 ? "No repositories found for this account."
            : CSharpOnly ? "None of your repositories match. GitHub lists C# only when it's a repository's main language; untick \"C# only\" to see everything."
            : "No repositories match your search.";
    }
}
