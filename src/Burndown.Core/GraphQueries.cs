namespace Burndown.Core;

public static class GraphQueries
{
    /// <summary>
    /// Finds types matching what a person would type: an exact id, a full name (<c>Shop.Order</c>),
    /// a simple name (<c>Order</c>, or <c>Repository</c> for <c>Repository&lt;T&gt;</c>), or a namespace
    /// suffix (<c>Data.Repository</c>). The most specific kind of match wins, so <c>Order</c> doesn't
    /// also return <c>OrderLine</c>. More than one result means the query is ambiguous.
    /// </summary>
    public static IReadOnlyList<CodeNode> FindTypes(this DependencyGraph graph, string query)
    {
        Func<CodeNode, bool>[] matchers =
        [
            n => n.Id == query,
            n => n.FullName == query || WithoutTypeParameters(n.FullName) == query,
            n => n.Name == query || WithoutTypeParameters(n.Name) == query,
            n => n.FullName.EndsWith("." + query, StringComparison.Ordinal)
                || WithoutTypeParameters(n.FullName).EndsWith("." + query, StringComparison.Ordinal),
        ];

        foreach (var matches in matchers)
        {
            var found = graph.Nodes.Where(matches).ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    private static string WithoutTypeParameters(string name)
    {
        var angle = name.IndexOf('<');
        return angle < 0 ? name : name[..angle];
    }
}
