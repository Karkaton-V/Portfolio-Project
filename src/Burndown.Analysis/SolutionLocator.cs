namespace Burndown.Analysis;

/// <summary>The input couldn't be resolved to a single solution or project. The message says what to do.</summary>
public sealed class SolutionNotFoundException(string message) : Exception(message);

/// <summary>
/// Turns what a person typed (nothing, a folder, or a file) into the solution or project file to analyze,
/// so <c>burndown analyze</c> works from the root of any repository.
/// </summary>
public static class SolutionLocator
{
    private static readonly string[] SolutionExtensions = [".slnx", ".sln"];
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "artifacts", "packages",
    };

    /// <summary>How deep below the given folder to look when it has no solution of its own.</summary>
    private const int SearchDepth = 3;

    /// <param name="input">A <c>.sln</c>, <c>.slnx</c>, or <c>.csproj</c> file, a folder, or null for the current folder.</param>
    /// <returns>The full path of the solution or project file.</returns>
    public static string Resolve(string? input)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(input) ? "." : input);

        if (File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            if (SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            throw new SolutionNotFoundException($"'{input}' isn't a solution or project. Pass a .sln, .slnx, or .csproj file, or a folder.");
        }

        if (!Directory.Exists(path))
        {
            throw new SolutionNotFoundException($"'{input}' doesn't exist.");
        }

        // 1. A solution in the folder itself. When "App.sln" and "App.slnx" sit side by side (common while
        //    migrating to .slnx), they describe the same solution; prefer the newer format.
        var solutions = PreferSlnx(SolutionsIn(path));
        if (solutions.Count == 1)
        {
            return solutions[0];
        }

        if (solutions.Count > 1)
        {
            throw Ambiguous(path, solutions);
        }

        // 2. A single project in the folder itself.
        var projects = Directory.GetFiles(path, "*.csproj");
        if (projects.Length == 1)
        {
            return projects[0];
        }

        // 3. A solution somewhere below (e.g. a repository whose solution lives in src/).
        var nested = PreferSlnx(FindBelow(path, SearchDepth));
        return nested.Count switch
        {
            1 => nested[0],
            0 when projects.Length > 1 => throw Ambiguous(path, projects),
            0 => throw new SolutionNotFoundException($"No .sln, .slnx, or .csproj file found in {path} or the folders below it."),
            _ => throw Ambiguous(path, nested),
        };
    }

    private static IEnumerable<string> SolutionsIn(string folder) =>
        SolutionExtensions.SelectMany(extension => Directory.GetFiles(folder, "*" + extension));

    private static IEnumerable<string> FindBelow(string folder, int depth)
    {
        if (depth == 0)
        {
            yield break;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.GetDirectories(folder).Where(d => !SkippedFolders.Contains(Path.GetFileName(d)));
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var solution in SolutionsIn(child))
            {
                yield return solution;
            }

            foreach (var deeper in FindBelow(child, depth - 1))
            {
                yield return deeper;
            }
        }
    }

    private static List<string> PreferSlnx(IEnumerable<string> solutions) => solutions
        .GroupBy(s => Path.Combine(Path.GetDirectoryName(s) ?? "", Path.GetFileNameWithoutExtension(s)), StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderBy(s => Path.GetExtension(s).Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static SolutionNotFoundException Ambiguous(string folder, IEnumerable<string> candidates) => new(
        $"Found more than one solution or project under {folder}; pass the one to analyze:{Environment.NewLine}" +
        string.Join(Environment.NewLine, candidates.Select(c => $"  {Path.GetRelativePath(folder, c)}")));
}
