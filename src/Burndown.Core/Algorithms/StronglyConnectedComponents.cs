namespace Burndown.Core.Algorithms;

/// <summary>Tarjan's strongly connected components algorithm, O(V + E).</summary>
public static class StronglyConnectedComponents
{
    /// <summary>
    /// Returns the groups of nodes that form cycles: every node in a group can reach every other.
    /// Single nodes are only returned when they have an edge to themselves.
    /// </summary>
    /// <remarks>
    /// The textbook version is recursive, with recursion depth equal to the longest path in the graph.
    /// A long dependency chain would overflow the call stack, so this version keeps its own explicit
    /// stack of (node, remaining successors) frames instead.
    /// </remarks>
    /// <param name="nodes">Starting points. Nodes reachable only through <paramref name="successors"/> are visited too.</param>
    public static IReadOnlyList<IReadOnlyList<T>> FindCycles<T>(IEnumerable<T> nodes, Func<T, IEnumerable<T>> successors)
        where T : notnull
    {
        var index = new Dictionary<T, int>();
        var lowLink = new Dictionary<T, int>();
        var componentStack = new Stack<T>();
        var onComponentStack = new HashSet<T>();
        var cycles = new List<IReadOnlyList<T>>();
        var nextIndex = 0;

        foreach (var root in nodes)
        {
            if (index.ContainsKey(root))
            {
                continue;
            }

            var callStack = new Stack<(T Node, IEnumerator<T> Successors)>();
            Enter(root);

            while (callStack.Count > 0)
            {
                var (node, remaining) = callStack.Peek();

                if (remaining.MoveNext())
                {
                    var successor = remaining.Current;
                    if (!index.ContainsKey(successor))
                    {
                        // Equivalent to the recursive call: suspend this frame and visit the successor.
                        Enter(successor);
                    }
                    else if (onComponentStack.Contains(successor))
                    {
                        lowLink[node] = Math.Min(lowLink[node], index[successor]);
                    }

                    continue;
                }

                // All successors done: equivalent to returning from the recursive call.
                remaining.Dispose();
                callStack.Pop();
                if (callStack.Count > 0)
                {
                    var caller = callStack.Peek().Node;
                    lowLink[caller] = Math.Min(lowLink[caller], lowLink[node]);
                }

                if (lowLink[node] == index[node])
                {
                    var component = PopComponent(node);
                    if (component.Count > 1 || successors(node).Contains(node))
                    {
                        cycles.Add(component);
                    }
                }
            }

            void Enter(T node)
            {
                index[node] = nextIndex;
                lowLink[node] = nextIndex;
                nextIndex++;
                componentStack.Push(node);
                onComponentStack.Add(node);
                callStack.Push((node, successors(node).GetEnumerator()));
            }
        }

        return cycles;

        List<T> PopComponent(T root)
        {
            var component = new List<T>();
            T member;
            do
            {
                member = componentStack.Pop();
                onComponentStack.Remove(member);
                component.Add(member);
            }
            while (!EqualityComparer<T>.Default.Equals(member, root));

            return component;
        }
    }
}
