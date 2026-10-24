using System.Text.RegularExpressions;

namespace CodeAtlas.Core;

/// <summary>
/// Leaves projects out of the analysis by name, with simple globs: <c>*</c> matches any run of characters
/// (dots included) and <c>?</c> one character, case-insensitively. <c>*Tests</c> matches both
/// <c>Shop.Tests</c> and <c>Shop.UnitTests</c>; <c>Samples.*</c> matches every project under Samples.
/// </summary>
public sealed class ProjectFilter
{
    private readonly IReadOnlyList<Regex> _excluded;

    public ProjectFilter(IEnumerable<string> excludePatterns)
    {
        Patterns = excludePatterns.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        _excluded = Patterns.Select(ToRegex).ToList();
    }

    public static ProjectFilter None { get; } = new([]);

    public IReadOnlyList<string> Patterns { get; }

    public bool Includes(string project) => !_excluded.Any(r => r.IsMatch(project));

    /// <summary>Removes excluded projects' types, and every dependency to or from them.</summary>
    public DependencyGraph Apply(DependencyGraph graph)
    {
        if (Patterns.Count == 0)
        {
            return graph;
        }

        var kept = graph.Nodes.Where(n => Includes(n.Project)).ToList();
        var keptIds = kept.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        return new DependencyGraph
        {
            Nodes = kept,
            Edges = graph.Edges.Where(e => keptIds.Contains(e.SourceId) && keptIds.Contains(e.TargetId)).ToList(),
            Warnings = graph.Warnings,
        };
    }

    private static Regex ToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
