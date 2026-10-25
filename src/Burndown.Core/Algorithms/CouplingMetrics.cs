namespace Burndown.Core.Algorithms;

/// <summary>How connected one type is to the rest of the solution.</summary>
/// <param name="Incoming">Afferent coupling (Ca): distinct solution types that depend on this one.</param>
/// <param name="Outgoing">Efferent coupling (Ce): distinct solution types this one depends on.</param>
/// <param name="Instability">
/// Ce / (Ca + Ce), from 0 (everything depends on it, it depends on nothing: hard to change) to 1
/// (nothing depends on it: easy to change). Robert C. Martin defined this for packages; applying it
/// to individual types is an adaptation, useful for spotting hotspots rather than as a strict rule.
/// </param>
public sealed record TypeCoupling(string NodeId, int Incoming, int Outgoing, double Instability);

public static class CouplingMetrics
{
    /// <summary>Calculates coupling for every type. Several edges between the same two types count once.</summary>
    public static IReadOnlyList<TypeCoupling> Calculate(DependencyGraph graph)
    {
        var pairs = graph.Edges.Select(e => (e.SourceId, e.TargetId)).Distinct().ToList();
        var incoming = pairs.CountBy(p => p.TargetId).ToDictionary(StringComparer.Ordinal);
        var outgoing = pairs.CountBy(p => p.SourceId).ToDictionary(StringComparer.Ordinal);

        return graph.Nodes
            .Select(node =>
            {
                var ca = incoming.GetValueOrDefault(node.Id);
                var ce = outgoing.GetValueOrDefault(node.Id);
                return new TypeCoupling(node.Id, ca, ce, ca + ce == 0 ? 0 : Math.Round((double)ce / (ca + ce), 2));
            })
            .OrderByDescending(c => c.Incoming + c.Outgoing)
            .ThenBy(c => c.NodeId, StringComparer.Ordinal)
            .ToList();
    }
}
