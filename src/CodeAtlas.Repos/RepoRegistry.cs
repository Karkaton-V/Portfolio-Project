using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAtlas.Repos;

/// <summary>A repository saved for quick access.</summary>
/// <param name="Name">Short unique name, used as <c>@name</c> on the command line.</param>
/// <param name="Path">The folder that contains the solution.</param>
/// <param name="GitHub">The GitHub repository it was cloned from (owner/name), if any.</param>
/// <param name="Managed">True when CodeAtlas cloned it into its own folder (and may delete it on removal).</param>
/// <param name="AnalyzedRemotely">True when the last analysis ran on GitHub's servers rather than this computer.</param>
public sealed record RegisteredRepo(
    string Name,
    string Path,
    string? GitHub,
    bool Managed,
    DateTimeOffset AddedAt,
    DateTimeOffset? SyncedAt = null,
    DateTimeOffset? AnalyzedAt = null,
    bool AnalyzedRemotely = false);

/// <summary>A registry entry couldn't be added or found. The message says why.</summary>
public sealed class RepoRegistryException(string message) : Exception(message);

/// <summary>
/// The saved repositories, shared by the command line and the desktop app. Stored as JSON in
/// <c>~/.codeatlas/repos.json</c> (or <c>$CODEATLAS_HOME</c>), next to the folders CodeAtlas clones
/// repositories into and writes reports to.
/// </summary>
public sealed partial class RepoRegistry
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly List<RegisteredRepo> _repos;

    private RepoRegistry(string home, List<RegisteredRepo> repos)
    {
        Home = home;
        _repos = repos;
    }

    public static string DefaultHome =>
        Environment.GetEnvironmentVariable("CODEATLAS_HOME") is { Length: > 0 } home
            ? home
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeatlas");

    public string Home { get; }

    /// <summary>Where repositories added from GitHub are cloned: <c>repos/owner/name</c>.</summary>
    public string ClonesDirectory => System.IO.Path.Combine(Home, "repos");

    /// <summary>Where the desktop app keeps each repository's latest report.</summary>
    public string ReportsDirectory => System.IO.Path.Combine(Home, "reports");

    public IReadOnlyList<RegisteredRepo> Repos => _repos;

    private string FilePath => System.IO.Path.Combine(Home, "repos.json");

    public static RepoRegistry Load(string? home = null)
    {
        home ??= DefaultHome;
        var file = System.IO.Path.Combine(home, "repos.json");
        if (!File.Exists(file))
        {
            return new RepoRegistry(home, []);
        }

        try
        {
            var repos = JsonSerializer.Deserialize<List<RegisteredRepo>>(File.ReadAllText(file), Options) ?? [];
            return new RepoRegistry(home, repos);
        }
        catch (JsonException ex)
        {
            throw new RepoRegistryException($"{file} is damaged ({ex.Message}). Fix or delete it to start over.");
        }
    }

    /// <summary>
    /// Renames a registry file that can't be read, so the app can start over with an empty list without losing
    /// what was in it. Returns the new file's path.
    /// </summary>
    public static string SetAsideDamaged(string? home = null)
    {
        home ??= DefaultHome;
        var file = System.IO.Path.Combine(home, "repos.json");
        var aside = System.IO.Path.Combine(home, $"repos.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Move(file, aside);
        return aside;
    }

    /// <summary>Writes to a temporary file first, so a crash mid-write can't leave a half-written registry.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Home);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_repos, Options));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public RegisteredRepo? Find(string name) =>
        _repos.FirstOrDefault(r => r.Name.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase));

    public RegisteredRepo? FindByPath(string path) =>
        _repos.FirstOrDefault(r => SamePath(r.Path, path));

    public RegisteredRepo Add(RegisteredRepo repo)
    {
        if (!ValidName().IsMatch(repo.Name))
        {
            throw new RepoRegistryException($"'{repo.Name}' isn't a valid name. Use letters, digits, '.', '-', or '_'.");
        }

        if (Find(repo.Name) is not null)
        {
            throw new RepoRegistryException($"A repo named '{repo.Name}' is already saved. Pick another name.");
        }

        if (FindByPath(repo.Path) is { } existing)
        {
            throw new RepoRegistryException($"That folder is already saved as '{existing.Name}'.");
        }

        _repos.Add(repo);
        _repos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return repo;
    }

    public void Update(RegisteredRepo repo)
    {
        var index = _repos.FindIndex(r => r.Name.Equals(repo.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new RepoRegistryException($"No saved repo named '{repo.Name}'.");
        }

        _repos[index] = repo;
    }

    public bool Remove(string name) =>
        _repos.RemoveAll(r => r.Name.Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Where a GitHub repository is cloned to.</summary>
    public string CloneDirectoryFor(GitHubRepoRef repo) => System.IO.Path.Combine(ClonesDirectory, repo.Owner, repo.Name);

    /// <summary>A short, unique, command-line friendly name: "Clean Architecture" becomes "clean-architecture".</summary>
    public string SuggestName(string basis)
    {
        var slug = Regex.Replace(basis.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.');
        if (slug.Length == 0)
        {
            slug = "repo";
        }

        var name = slug;
        for (var n = 2; Find(name) is not null; n++)
        {
            name = $"{slug}-{n}";
        }

        return name;
    }

    private static bool SamePath(string a, string b) => string.Equals(
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidName();
}
