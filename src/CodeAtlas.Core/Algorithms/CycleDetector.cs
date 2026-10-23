namespace CodeAtlas.Core.Algorithms;

/// <summary>The level at which dependencies are grouped before looking for cycles.</summary>
public enum CycleLevel
{
    /// <summary>Group types by namespace. Cycles here usually mean layers are tangled.</summary>
    Namespace,

    /// <summary>Individual types. Often legitimate (e.g. Order and OrderLine pointing at each other).</summary>
    Type,
}

/// <summary>A set of groups (namespaces or types) that all depend on each other, directly or indirectly.</summary>
/// <param name="Members">The namespaces or type ids in the cycle, sorted.</param>
/// <param name="ExamplePath">One shortest loop through the cycle, starting and ending at the first member.</param>
/// <param name="Evidence">The type-level edges between members that create the cycle.</param>
public sealed record DependencyCycle(
    IReadOnlyList<string> Members,
    IReadOnlyList<string> ExamplePath,
    IReadOnlyList<CodeEdge> Evidence);

public static class CycleDetector
{
    /// <summary>Finds cycles, largest first.</summary>
    public static IReadOnlyList<DependencyCycle> FindCycles(DependencyGraph graph, CycleLevel level)
    {
        var groupOf = graph.Nodes.ToDictionary(
            n => n.Id,
            n => level == CycleLevel.Namespace ? n.Namespace : n.Id,
            StringComparer.Ordinal);

        // Edges inside one group can't create a cycle between groups (and every namespace "depends on itself").
        var crossingEdges = graph.Edges
            .Where(e => groupOf[e.SourceId] != groupOf[e.TargetId])
            .ToList();

        var successors = crossingEdges
            .GroupBy(e => groupOf[e.SourceId], StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => groupOf[e.TargetId]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        IEnumerable<string> SuccessorsOf(string group) => successors.GetValueOrDefault(group) ?? [];

        var groups = groupOf.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        return StronglyConnectedComponents.FindCycles(groups, SuccessorsOf)
            .Select(component =>
            {
                var members = component.Order(StringComparer.Ordinal).ToList();
                var memberSet = members.ToHashSet(StringComparer.Ordinal);
                var evidence = crossingEdges
                    .Where(e => memberSet.Contains(groupOf[e.SourceId]) && memberSet.Contains(groupOf[e.TargetId]))
                    .ToList();

                return new DependencyCycle(members, ShortestLoop(members[0], memberSet, SuccessorsOf), evidence);
            })
            .OrderByDescending(c => c.Members.Count)
            .ThenBy(c => c.Members[0], StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Breadth-first search from <paramref name="start"/> back to itself, staying inside the cycle.</summary>
    private static List<string> ShortestLoop(
        string start, HashSet<string> members, Func<string, IEnumerable<string>> successors)
    {
        var cameFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>([start]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in successors(current).Where(members.Contains))
            {
                if (next == start)
                {
                    var path = new List<string> { start };
                    for (var step = current; step != start; step = cameFrom[step])
                    {
                        path.Add(step);
                    }

                    path.Add(start);
                    path.Reverse();
                    return path;
                }

                if (cameFrom.TryAdd(next, current))
                {
                    queue.Enqueue(next);
                }
            }
        }

        // Unreachable for a real strongly connected component.
        return [start];
    }
}
