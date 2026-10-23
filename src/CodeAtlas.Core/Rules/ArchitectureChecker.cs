using CodeAtlas.Core.Algorithms;

namespace CodeAtlas.Core.Rules;

/// <summary>A rule as reported to the user and to SARIF consumers.</summary>
public sealed record RuleInfo(string Id, string Name, string Description, RuleSeverity Severity);

/// <param name="Location">Where to point the reader: the first offending line, or the source type's declaration.</param>
/// <param name="NodeId">The type that breaks the rule, so a viewer can jump to it.</param>
public sealed record RuleViolation(
    string RuleId,
    RuleSeverity Severity,
    string Message,
    SourceLocation? Location,
    string? NodeId = null);

/// <param name="Rules">Every rule that was checked, including those with no violations.</param>
/// <param name="Warnings">Problems with the config itself, such as a selector that matches nothing.</param>
public sealed record CheckResult(
    IReadOnlyList<RuleInfo> Rules,
    IReadOnlyList<RuleViolation> Violations,
    IReadOnlyList<string> Warnings)
{
    public int ErrorCount => Violations.Count(v => v.Severity == RuleSeverity.Error);

    public int WarningCount => Violations.Count(v => v.Severity == RuleSeverity.Warning);
}

public static class ArchitectureChecker
{
    public const string NamespaceCycleRuleId = "namespace-cycle";

    public static CheckResult Check(DependencyGraph graph, ArchitectureConfig config)
    {
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var rules = new List<RuleInfo>();
        var violations = new List<RuleViolation>();
        var warnings = new List<string>();

        foreach (var rule in config.Rules)
        {
            rules.Add(new RuleInfo(rule.RuleId, rule.Name, Describe(rule), rule.Severity));

            // Resolve each selector to the set of type ids it matches, once. Checking an edge is then two
            // hash lookups instead of running every pattern's regex against both ends of every edge.
            var from = MatchingIds(graph, Selector.Resolve(rule.From, config.Layers));
            var targets = rule.Targets.Select(t => MatchingIds(graph, Selector.Resolve(t, config.Layers))).ToList();

            // A typo in a pattern would make the rule silently pass forever, so call it out.
            foreach (var (text, ids) in targets.Prepend(from).Where(s => s.Ids.Count == 0))
            {
                warnings.Add($"'{text}' in rule '{rule.Name}' matches no types. Check it for typos.");
            }

            // The forbidden types for a `forbid` rule, or the allowed ones for a `mayOnlyDependOn` rule.
            var listed = targets.SelectMany(t => t.Ids).ToHashSet(StringComparer.Ordinal);
            bool Violates(string targetId) => rule.Forbid is not null
                ? listed.Contains(targetId)
                : !from.Ids.Contains(targetId) && !listed.Contains(targetId);

            // One violation per pair of types, however many ways the source uses the target.
            var offendingPairs = graph.Edges
                .Where(e => rule.Kinds is null || rule.Kinds.Contains(e.Kind))
                .Where(e => from.Ids.Contains(e.SourceId) && Violates(e.TargetId))
                .GroupBy(e => (e.SourceId, e.TargetId));

            foreach (var pair in offendingPairs)
            {
                var edges = pair.OrderBy(e => e.Kind).ToList();
                var source = nodes[pair.Key.SourceId];
                var target = nodes[pair.Key.TargetId];

                violations.Add(new RuleViolation(
                    rule.RuleId,
                    rule.Severity,
                    $"{source.FullName} depends on {target.FullName} ({string.Join(", ", edges.Select(e => e.Kind))}).",
                    edges.Select(e => e.Location).FirstOrDefault(l => l is not null) ?? source.Locations.FirstOrDefault(),
                    source.Id));
            }
        }

        if (config.NamespaceCycles is { } cycleSeverity)
        {
            rules.Add(new RuleInfo(
                NamespaceCycleRuleId,
                "No namespace cycles",
                "Namespaces must not depend on each other in a loop.",
                cycleSeverity));

            foreach (var cycle in CycleDetector.FindCycles(graph, CycleLevel.Namespace))
            {
                // Point at a line that makes the first hop of the example loop.
                var firstHop = cycle.Evidence.First(e =>
                    nodes[e.SourceId].Namespace == cycle.ExamplePath[0] && nodes[e.TargetId].Namespace == cycle.ExamplePath[1]);

                violations.Add(new RuleViolation(
                    NamespaceCycleRuleId,
                    cycleSeverity,
                    $"Namespace cycle: {string.Join(" -> ", cycle.ExamplePath.Select(DisplayNamespace))}. " +
                    $"{cycle.Evidence.Count} dependencies are involved, including " +
                    $"{nodes[firstHop.SourceId].FullName} -> {nodes[firstHop.TargetId].FullName} ({firstHop.Kind}).",
                    firstHop.Location ?? nodes[firstHop.SourceId].Locations.FirstOrDefault(),
                    firstHop.SourceId));
            }
        }

        return new CheckResult(rules, violations, warnings.Distinct().ToList());
    }

    private static (string Text, HashSet<string> Ids) MatchingIds(DependencyGraph graph, Selector selector) =>
        (selector.Text, graph.Nodes.Where(selector.Matches).Select(n => n.Id).ToHashSet(StringComparer.Ordinal));

    private static string Describe(DependencyRule rule)
    {
        var description = rule.Forbid is not null
            ? $"Types in {rule.From} must not depend on {string.Join(", ", rule.Forbid)}."
            : $"Types in {rule.From} may only depend on {string.Join(", ", rule.Targets.Prepend(rule.From))}.";

        return rule.Kinds is null ? description : $"{description} Applies to: {string.Join(", ", rule.Kinds)}.";
    }

    private static string DisplayNamespace(string name) => name.Length == 0 ? "(global namespace)" : name;
}
