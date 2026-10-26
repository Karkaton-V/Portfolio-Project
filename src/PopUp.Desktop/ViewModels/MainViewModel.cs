using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using PopUp.Analysis;
using PopUp.Desktop.Services;
using PopUp.Repos;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PopUp.Desktop.ViewModels;

/// <summary>
/// The main window: the saved repositories, the selected one's report, and the long-running work
/// (cloning, restoring, analyzing) with its progress. Dialogs and confirmations stay in the view.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly System.Net.Http.HttpClient Http = new();

    private readonly RepoRegistry _registry;
    private readonly PopUpSettings _settings;
    private CancellationTokenSource? _work;

    public MainViewModel(RepoRegistry registry)
    {
        _registry = registry;
        _settings = PopUpSettings.Load(registry.Home);
        foreach (var repo in registry.Repos)
        {
            Repos.Add(new RepoViewModel(repo, registry.ReportsDirectory));
        }

        SelectedRepo = Repos.FirstOrDefault();
        ShowSelected();
    }

    public ObservableCollection<RepoViewModel> Repos { get; } = [];

    public RepoRegistry Registry => _registry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(SyncCommand), nameof(OpenFolderCommand), nameof(OpenInBrowserCommand))]
    [NotifyPropertyChangedFor(nameof(CanLocate))]
    private RepoViewModel? _selectedRepo;

    public bool CanLocate => SelectedRepo is not null && !IsBusy;

    /// <summary>The report the viewer shows; null shows the placeholder instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReport), nameof(ShowPlaceholder))]
    private Uri? _reportUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReport), nameof(ShowPlaceholder), nameof(IsIdle), nameof(CanLocate))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(SyncCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _placeholderTitle = "";

    [ObservableProperty]
    private string _placeholderText = "";

    /// <summary>One line under the sidebar: what's happening, or the last result.</summary>
    [ObservableProperty]
    private string _status = "";

    public bool ShowReport => ReportUri is not null && !IsBusy;

    public bool ShowPlaceholder => !ShowReport;

    public bool IsIdle => !IsBusy;

    partial void OnSelectedRepoChanged(RepoViewModel? value)
    {
        Status = value?.AnalyzedText ?? "";
        ShowSelected();
    }

    /// <summary>Shows the selected repository's last report, or explains what to do next.</summary>
    private void ShowSelected()
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedRepo is null)
        {
            ReportUri = null;
            PlaceholderTitle = Repos.Count == 0 ? "Add a repository to get started" : "Pick a repository";
            PlaceholderText = Repos.Count == 0
                ? "Add a folder that contains a C# solution, or pick one of your GitHub repositories. PopUp maps how its types depend on each other."
                : "Choose a repository on the left to see its dependency map.";
            return;
        }

        if (SelectedRepo.HasReport)
        {
            // The query string changes whenever the report is rewritten, so the viewer reloads it.
            // #embedded=1 tells the report it's inside the app, which shows its own branding.
            var version = File.GetLastWriteTimeUtc(SelectedRepo.ReportPath).Ticks;
            ReportUri = new Uri(new Uri(SelectedRepo.ReportPath).AbsoluteUri + "?v=" + version + "#embedded=1");
        }
        else
        {
            ReportUri = null;
            PlaceholderTitle = $"{SelectedRepo.Name} hasn't been analyzed yet";
            PlaceholderText = "Press Analyze to map its dependencies. Large solutions take a minute or two.";
        }
    }

    public async Task AddFolderAsync(string folder)
    {
        await RunAsync($"Adding {Path.GetFileName(folder)}", async (progress, token) =>
        {
            SolutionLocator.Resolve(folder); // fail now, with a clear message, if there's nothing to analyze
            // A clone of a GitHub repository stays linked to it, so it can be analyzed on GitHub if the folder goes away.
            return Save(new RegisteredRepo(
                _registry.SuggestName(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))),
                Path.GetFullPath(folder), LocalRepoFinder.ReadOrigin(folder)?.FullName, Managed: false, AddedAt: DateTimeOffset.Now));
        }, analyzeAfter: true);
    }

    /// <summary>
    /// Adds a GitHub repository, preferring a copy already on this computer (<paramref name="localPath"/>,
    /// e.g. one GitHub Desktop cloned), then a fresh shallow clone. If a clone can't be made (no git, no
    /// access from this machine), the repository is saved anyway and analyzed on GitHub's servers.
    /// </summary>
    public async Task AddFromGitHubAsync(GitHubRepoRef gitHub, string? localPath)
    {
        await RunAsync($"Adding {gitHub.FullName}", async (progress, token) =>
        {
            if (localPath is not null)
            {
                SolutionLocator.Resolve(localPath);
                return Save(new RegisteredRepo(
                    _registry.SuggestName(gitHub.Name), localPath, gitHub.FullName, Managed: false, AddedAt: DateTimeOffset.Now));
            }

            var destination = _registry.CloneDirectoryFor(gitHub);
            var cloned = false;
            if (!Directory.Exists(Path.Combine(destination, ".git")))
            {
                progress.Report($"Downloading {gitHub.FullName}…");
                try
                {
                    await Tools.CloneAsync(gitHub, destination, token);
                    cloned = true;
                }
                catch (ToolException)
                {
                    // Can't make a local copy: keep the repository and let analysis run on GitHub instead.
                    return Save(new RegisteredRepo(
                        _registry.SuggestName(gitHub.Name), destination, gitHub.FullName, Managed: true, AddedAt: DateTimeOffset.Now));
                }
            }

            string solution;
            try
            {
                solution = SolutionLocator.Resolve(destination);
            }
            catch (SolutionNotFoundException) when (cloned)
            {
                // Nothing to analyze (e.g. a Unity project, which doesn't commit .sln files): don't leave the
                // download behind.
                Tools.DeleteClone(destination);
                throw;
            }

            progress.Report("Restoring NuGet packages…");
            await Tools.RestoreAsync(solution, token);

            return Save(new RegisteredRepo(
                _registry.SuggestName(gitHub.Name), destination, gitHub.FullName, Managed: true, AddedAt: DateTimeOffset.Now));
        }, analyzeAfter: true);
    }

    /// <summary>Points the selected repository at a folder where it already lives on this computer.</summary>
    public async Task LocateAsync(RepoViewModel repo, string folder)
    {
        await RunAsync($"Using {Path.GetFileName(folder)} for {repo.Name}", (progress, token) =>
        {
            SolutionLocator.Resolve(folder);
            var located = repo.Repo with
            {
                Path = Path.GetFullPath(folder),
                Managed = false,
                GitHub = repo.Repo.GitHub ?? LocalRepoFinder.ReadOrigin(folder)?.FullName,
            };
            _registry.Update(located);
            _registry.Save();
            repo.Update(located);
            return Task.FromResult(located);
        }, analyzeAfter: true);
    }

    private RegisteredRepo Save(RegisteredRepo repo)
    {
        _registry.Add(repo);
        _registry.Save();
        return repo;
    }

    private bool CanActOnSelection() => SelectedRepo is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task AnalyzeAsync() => AnalyzeSelectedAsync();

    private bool CanSync() => SelectedRepo is { IsGitHub: true, HasLocalCopy: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        if (SelectedRepo is not { } selected)
        {
            return;
        }

        await RunAsync($"Updating {selected.Name}", async (progress, token) =>
        {
            progress.Report("Pulling the latest changes…");
            await Tools.PullAsync(selected.Repo.Path, token);
            progress.Report("Restoring NuGet packages…");
            await Tools.RestoreAsync(SolutionLocator.Resolve(selected.Repo.Path), token);
            var synced = selected.Repo with { SyncedAt = DateTimeOffset.Now };
            _registry.Update(synced);
            _registry.Save();
            return synced;
        }, analyzeAfter: true);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _work?.Cancel();

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenFolder()
    {
        if (SelectedRepo is { } selected && Directory.Exists(selected.Repo.Path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { selected.Repo.Path } });
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenInBrowser()
    {
        if (SelectedRepo is { HasReport: true } selected)
        {
            Process.Start(new ProcessStartInfo(selected.ReportPath) { UseShellExecute = true });
        }
    }

    private bool CanOpen() => SelectedRepo is not null;

    /// <summary>Removes a repository from the list. A clone PopUp made can be deleted too.</summary>
    public void Remove(RepoViewModel repo, bool deleteClone)
    {
        _registry.Remove(repo.Name);
        _registry.Save();
        Repos.Remove(repo);
        TryDelete(repo.ReportPath);

        // Only ever delete inside PopUp's own clones folder, never a folder the user added themselves.
        if (deleteClone && repo.Repo.Managed && IsInside(repo.Repo.Path, _registry.ClonesDirectory))
        {
            Tools.DeleteClone(repo.Repo.Path);
        }

        SelectedRepo = Repos.FirstOrDefault();
        ShowSelected();
        Status = $"Removed {repo.Name}";
    }

    private async Task AnalyzeSelectedAsync()
    {
        if (SelectedRepo is not { } selected)
        {
            return;
        }

        await RunAsync($"Analyzing {selected.Name}", async (progress, token) =>
        {
            var repo = selected.Repo;
            Exception? localError = null;

            // 1. Its folder is gone (moved, deleted)? Look for another clone of it on this computer.
            if (!Directory.Exists(repo.Path) && repo.GitHub is { } fullName)
            {
                progress.Report("Looking for a copy on this computer…");
                if (await Task.Run(() => LocalRepoFinder.Find().GetValueOrDefault(fullName), token) is [var found, ..])
                {
                    repo = repo with { Path = found, Managed = false };
                    _registry.Update(repo);
                }
            }

            // 2. A local copy: analyze it here.
            if (Directory.Exists(repo.Path))
            {
                try
                {
                    // Roslyn and MSBuild do heavy synchronous work; keep it off the UI thread.
                    var summary = await Task.Run(() => RepoAnalyzer.AnalyzeAsync(repo, selected.ReportPath, progress, token), token);
                    var rules = summary.Errors is { } errors ? $" · {errors} rule error(s), {summary.Warnings} warning(s)" : "";
                    return Analyzed(selected, repo, remotely: false, $"{summary.Types} types, {summary.Dependencies} dependencies{rules}");
                }
                catch (Exception ex) when (repo.GitHub is not null && ex is not OperationCanceledException and not SolutionNotFoundException)
                {
                    // e.g. no .NET SDK on this computer: GitHub's machines have one.
                    localError = ex;
                    progress.Report($"Couldn't analyze it here ({ex.Message}). Trying on GitHub…");
                }
            }
            else if (repo.GitHub is null)
            {
                throw new DirectoryNotFoundException($"{repo.Path} doesn't exist anymore. Use Locate to point PopUp at where it is now.");
            }

            // 3. No usable local copy of a GitHub repository: analyze it on GitHub's servers.
            try
            {
                await AnalyzeOnGitHubAsync(repo, selected.ReportPath, progress, token);
            }
            catch (Exception ex) when (localError is not null && ex is not OperationCanceledException)
            {
                // Both failed: the local error usually explains more, so show it first.
                throw new InvalidOperationException(
                    $"Couldn't analyze it on this computer: {localError.Message}\n\nThe fallback to GitHub failed too: {ex.Message}", localError);
            }

            return Analyzed(selected, repo, remotely: true, $"Analyzed on GitHub ({_settings.Runner} runs the workflow)");
        }, analyzeAfter: false);
    }

    private RegisteredRepo Analyzed(RepoViewModel item, RegisteredRepo repo, bool remotely, string status)
    {
        var analyzed = repo with { AnalyzedAt = DateTimeOffset.Now, AnalyzedRemotely = remotely };
        _registry.Update(analyzed);
        _registry.Save();
        item.Update(analyzed);
        Status = status;
        return analyzed;
    }

    private async Task AnalyzeOnGitHubAsync(RegisteredRepo repo, string reportPath, IProgress<string> progress, CancellationToken token)
    {
        if (!GitHubRepoRef.TryParse(repo.GitHub, out var target))
        {
            throw new InvalidOperationException($"{repo.Name} isn't linked to a GitHub repository.");
        }

        var gitHubToken = await Tools.FindGitHubTokenAsync(token)
            ?? throw new GitHubException("Analyzing on GitHub needs a GitHub sign-in: install the GitHub CLI and run `gh auth login`.");
        if (!GitHubRepoRef.TryParse(_settings.Runner, out var runner))
        {
            throw new GitHubException($"The runner repository '{_settings.Runner}' in settings.json isn't owner/name.");
        }

        var analyzer = new RemoteAnalyzer(new GitHubClient(Http, gitHubToken), runner);
        await analyzer.AnalyzeAsync(target, reportPath, progress, token);
    }

    /// <summary>
    /// Runs one piece of long work with progress shown in the placeholder, cancellation, and errors turned
    /// into a readable message. A newly added repository is selected and, optionally, analyzed next.
    /// </summary>
    private async Task RunAsync(
        string title,
        Func<IProgress<string>, CancellationToken, Task<RegisteredRepo>> work,
        bool analyzeAfter)
    {
        _work = new CancellationTokenSource();
        IsBusy = true;
        PlaceholderTitle = title;
        PlaceholderText = "Starting…";
        var progress = new Progress<string>(message => PlaceholderText = message);

        RegisteredRepo? result = null;
        try
        {
            result = await work(progress, _work.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
        }
        // Any failure (a missing tool, a broken project, an MSBuild error) is shown where the report would be,
        // so the window stays usable and the user can fix it and try again.
        catch (Exception ex)
        {
            Status = "Something went wrong";
            IsBusy = false;
            ReportUri = null;
            PlaceholderTitle = $"{title} didn't work";
            PlaceholderText = ex.Message;
            return;
        }
        finally
        {
            _work.Dispose();
            _work = null;
            IsBusy = false;
        }

        if (result is not null)
        {
            var item = Repos.FirstOrDefault(r => r.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                // Keep the sidebar in the registry's (alphabetical) order.
                item = new RepoViewModel(result, _registry.ReportsDirectory);
                var index = _registry.Repos.ToList().FindIndex(r => r.Name == result.Name);
                Repos.Insert(Math.Clamp(index, 0, Repos.Count), item);
            }
            else
            {
                item.Update(result);
            }

            SelectedRepo = item;
        }

        ShowSelected();

        if (analyzeAfter && result is not null)
        {
            await AnalyzeSelectedAsync();
        }
    }

    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && relative != ".";
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
