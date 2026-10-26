namespace PopUp.Analysis;

public static class ConfigLocator
{
    public const string FileName = "popup.json";

    /// <summary>Names from before PopUp was renamed (it was CodeAtlas, then Burndown); still read so old configs keep working.</summary>
    public static readonly IReadOnlyList<string> LegacyFileNames = ["burndown.json", "codeatlas.json"];

    /// <summary>
    /// Looks for popup.json in <paramref name="directory"/> and its parents, the way tools find
    /// .editorconfig, so a solution in src/ can use a config at the repository root. Stops at the repository
    /// root (the folder with .git) so a config from an unrelated parent folder never applies.
    /// </summary>
    public static string? Find(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            foreach (var name in (string[])[FileName, .. LegacyFileNames])
            {
                var candidate = Path.Combine(current.FullName, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }
}
