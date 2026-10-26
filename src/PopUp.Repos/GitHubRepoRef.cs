using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace PopUp.Repos;

/// <summary>A repository on github.com, identified by owner and name.</summary>
public sealed partial record GitHubRepoRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public string CloneUrl => $"https://github.com/{Owner}/{Name}.git";

    public string WebUrl => $"https://github.com/{Owner}/{Name}";

    /// <summary>
    /// Accepts the ways people paste a repository: <c>owner/name</c>, <c>github:owner/name</c>,
    /// <c>https://github.com/owner/name</c> (with or without <c>.git</c>, or a deeper page like <c>/tree/main</c>),
    /// and <c>git@github.com:owner/name.git</c>.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out GitHubRepoRef? repo)
    {
        repo = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Pattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups["name"].Value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        // "." and ".." are valid name characters but not valid repository names.
        if (name.Length == 0 || name.Trim('.').Length == 0)
        {
            return false;
        }

        repo = new GitHubRepoRef(match.Groups["owner"].Value, name);
        return true;
    }

    public override string ToString() => FullName;

    // Owners: letters, digits, and single hyphens, up to 39 characters. Names: letters, digits, '.', '-', '_'.
    [GeneratedRegex(
        @"^(?:(?:https?://)?(?:www\.)?github\.com[/:]|git@github\.com:|github:)?(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?)/(?<name>[A-Za-z0-9._-]+?)(?:\.git)?(?:/.*)?/?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
