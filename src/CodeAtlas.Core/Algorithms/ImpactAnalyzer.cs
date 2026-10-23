namespace CodeAtlas.Core.Algorithms;

/// <summary>A type that might need to change, or be retested, when the target changes.</summary>
/// <param name="Distance">1 for direct dependents, 2 for their dependents, and so on.</param>
/// <param name="Path">The shortest chain of edges from this type down to the target.</param>
public sealed record ImpactedType(string NodeId, int Distance, IReadOnlyList<CodeEdge> Path);

public static class ImpactAnalyzer
{
    /// <summary>
    /// Finds every type that depends on <paramref name="targetId"/>, directly or transitively, by walking
    /// dependency edges backwards with a breadth-first search. BFS visits types in order of distance, so
    /// the first path found to each type is a shortest one, and a visited set stops cycles from looping.
    /// </summary>
    /// <remarks>
    /// These are <em>potentially</em> affected types. Whether a change actually breaks a dependent depends
    /// on what changed: an internal implementation detail may affect nothing, a public signature a lot.
    /// </remarks>
    /// <param name="maxDepth">Stop after this many steps. Null means no limit.</param>
    /// <param name="kinds">Only follow these kinds of dependency. Null means all.</param>
    public static IReadOnlyList<ImpactedType> FindDependents(
        DependencyGraph graph,
        string targetId,
        int? maxDepth = null,
        IReadOnlySet<DependencyKind>? kinds = null)
    {
        if (!graph.Nodes.Any(n => n.Id == targetId))
        {
            throw new ArgumentException($"The graph has no type with id '{targetId}'.", nameof(targetId));
        }

        var incoming = graph.Edges
            .Where(e => kinds is null || kinds.Contains(e.Kind))
            .ToLookup(e => e.TargetId, StringComparer.Ordinal);

        // For each dependent found, the edge that led to it (dependent -> one step closer to the target).
        var reachedBy = new Dictionary<string, CodeEdge>(StringComparer.Ordinal);
        var distance = new Dictionary<string, int>(StringComparer.Ordinal) { [targetId] = 0 };
        var queue = new Queue<string>([targetId]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (distance[current] == maxDepth)
            {
                continue;
            }

            foreach (var edge in incoming[current].OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.Kind))
            {
                var dependent = edge.SourceId;
                if (distance.TryAdd(dependent, distance[current] + 1))
                {
                    reachedBy[dependent] = edge;
                    queue.Enqueue(dependent);
                }
            }
        }

        return reachedBy.Keys
            .Select(id => new ImpactedType(id, distance[id], PathToTarget(id)))
            .OrderBy(i => i.Distance)
            .ThenBy(i => i.NodeId, StringComparer.Ordinal)
            .ToList();

        List<CodeEdge> PathToTarget(string id)
        {
            var path = new List<CodeEdge>();
            while (id != targetId)
            {
                var edge = reachedBy[id];
                path.Add(edge);
                id = edge.TargetId;
            }

            return path;
        }
    }
}
