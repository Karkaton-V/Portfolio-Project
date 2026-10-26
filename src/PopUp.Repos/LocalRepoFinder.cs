using System.Text.RegularExpressions;

namespace PopUp.Repos;

/// <summary>
/// Finds clones of GitHub repositories that already exist on this computer, so PopUp can use them
/// instead of downloading another copy. A folder counts when it's a Git repository whose <c>origin</c>
/// remote points at github.com.
/// </summary>
/// <remarks>
/// GitHub Desktop has no public way for other apps to read its repository list, but it clones into
/// <c>Documents\GitHub</c> by default, so searching that folder finds what it manages. Visual Studio's
/// default (<c>source\repos</c>) is searched too, and any folder the user points PopUp at with Locate.
/// </remarks>
public static partial class LocalRepoFinder
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".vs", ".idea", "packages", "artifacts", "Library", "Temp",
    };

    /// <summary>Where clones usually live: GitHub Desktop's and Visual Studio's default folders, and common habits.</summary>
    public static IReadOnlyList<string> DefaultSearchFolders
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return
            [
                Path.Combine(documents, "GitHub"), // GitHub Desktop
                Path.Combine(home, "source", "repos"), // Visual Studio
                Path.Combine(home, "repos"),
                Path.Combine(home, "GitHub"),
                Path.Combine(home, "dev"),
                Path.Combine(home, "code"),
            ];
        }
    }

    /// <summary>
    /// Maps "owner/name" (case-insensitive) to the local folders that are clones of it, searching each folder
    /// and its subfolders a couple of levels down. Missing or unreadable folders are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Find(IEnumerable<string>? folders = null, int depth = 2)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders ?? DefaultSearchFolders)
        {
            Search(Path.GetFullPath(folder), depth);
        }

        return found.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase);

        void Search(string folder, int levelsLeft)
        {
            if (!visited.Add(folder) || !Directory.Exists(folder))
            {
                return;
            }

            if (ReadOrigin(folder) is { } origin)
            {
                if (!found.TryGetValue(origin.FullName, out var paths))
                {
                    found[origin.FullName] = paths = [];
                }

                paths.Add(folder);
                return; // a repository's subfolders aren't separate repositories
            }

            if (levelsLeft == 0)
            {
                return;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.GetDirectories(folder);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var child in children.Where(c => !SkippedFolders.Contains(Path.GetFileName(c))))
            {
                Search(child, levelsLeft - 1);
            }
        }
    }

    /// <summary>The GitHub repository a local clone's <c>origin</c> remote points at, or null.</summary>
    public static GitHubRepoRef? ReadOrigin(string folder)
    {
        var config = ConfigPath(folder);
        if (config is null || !File.Exists(config))
        {
            return null;
        }

        try
        {
            return GitHubRepoRef.TryParse(ParseOriginUrl(File.ReadAllText(config)), out var repo) ? repo : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a repository's Git config lives. Usually <c>.git/config</c>, but in a worktree or submodule
    /// <c>.git</c> is a file pointing elsewhere (<c>gitdir: ...</c>), and a worktree's remotes live in the
    /// main repository's config (found through its <c>commondir</c> file).
    /// </summary>
    internal static string? ConfigPath(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        if (Directory.Exists(dotGit))
        {
            return Path.Combine(dotGit, "config");
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        var pointer = File.ReadAllText(dotGit).Trim();
        if (!pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gitDir = Path.GetFullPath(Path.Combine(folder, pointer["gitdir:".Length..].Trim()));
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirFile).Trim()));
        }

        return Path.Combine(gitDir, "config");
    }

    /// <summary>The <c>url</c> in the <c>[remote "origin"]</c> section of a Git config file, or null.</summary>
    internal static string? ParseOriginUrl(string config)
    {
        var inOrigin = false;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = OriginSection().IsMatch(line);
                continue;
            }

            if (inOrigin && UrlLine().Match(line) is { Success: true } match)
            {
                return match.Groups["url"].Value.Trim();
            }
        }

        return null;
    }

    [GeneratedRegex("""^\[\s*remote\s+"origin"\s*\]$""", RegexOptions.IgnoreCase)]
    private static partial Regex OriginSection();

    [GeneratedRegex(@"^url\s*=\s*(?<url>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLine();
}
