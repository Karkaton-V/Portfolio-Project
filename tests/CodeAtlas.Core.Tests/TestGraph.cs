namespace CodeAtlas.Core.Tests;

/// <summary>Builds small graphs from readable specs like <c>"Orders.Api -> Orders.Domain"</c>.</summary>
internal static class TestGraph
{
    /// <param name="specs">
    /// <c>"A.X -> B.Y"</c> (a References edge), <c>"A.X -Field-> B.Y"</c> (a specific kind), or <c>"A.X"</c>
    /// (a node with no edges). Node ids are the full names; the namespace is everything before the last dot.
    /// </param>
    public static DependencyGraph Create(params string[] specs)
    {
        var nodes = new SortedSet<string>(StringComparer.Ordinal);
        var edges = new List<CodeEdge>();

        foreach (var spec in specs)
        {
            var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            nodes.Add(parts[0]);

            if (parts.Length == 3)
            {
                var kind = parts[1] == "->" ? DependencyKind.References : Enum.Parse<DependencyKind>(parts[1].Trim('-', '>'));
                nodes.Add(parts[2]);
                edges.Add(new CodeEdge(parts[0], parts[2], kind, Location: null));
            }
        }

        return new DependencyGraph
        {
            Nodes = nodes.Select(CreateNode).ToList(),
            Edges = edges,
        };
    }

    /// <summary>A straight line of <paramref name="length"/> types, each depending on the next.</summary>
    public static DependencyGraph Chain(int length, bool closeLoop = false)
    {
        var names = Enumerable.Range(0, length).Select(i => $"Chain.T{i}").ToList();
        var edges = names.Zip(names.Skip(1), (from, to) => new CodeEdge(from, to, DependencyKind.References, null)).ToList();
        if (closeLoop)
        {
            edges.Add(new CodeEdge(names[^1], names[0], DependencyKind.References, null));
        }

        return new DependencyGraph { Nodes = names.Select(CreateNode).ToList(), Edges = edges };
    }

    private static CodeNode CreateNode(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return new CodeNode(
            Id: fullName,
            Name: fullName[(lastDot + 1)..],
            FullName: fullName,
            Namespace: lastDot < 0 ? "" : fullName[..lastDot],
            Project: "Test",
            Kind: NodeKind.Class,
            Locations: []);
    }
}
