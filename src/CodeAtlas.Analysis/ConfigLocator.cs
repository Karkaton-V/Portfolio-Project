namespace CodeAtlas.Analysis;

public static class ConfigLocator
{
    public const string FileName = "codeatlas.json";

    /// <summary>
    /// Looks for codeatlas.json in <paramref name="directory"/> and its parents, the way tools find
    /// .editorconfig, so a solution in src/ can use a config at the repository root. Stops at the repository
    /// root (the folder with .git) so a config from an unrelated parent folder never applies.
    /// </summary>
    public static string? Find(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }
}
