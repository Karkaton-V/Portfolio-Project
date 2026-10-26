using PopUp.Core;
using PopUp.Core.Algorithms;

namespace PopUp.Cli.Commands;

internal static class ImpactCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>
        {
            ["--depth"] = "depth",
            ["--kinds"] = "kinds",
            ["--config"] = "config",
            ["--exclude"] = "exclude",
        });
        arguments.ExpectPositionals(1, "input", "type");

        // `impact Order` analyzes the current folder; `impact graph.json Order` names the input.
        var (input, typeQuery) = arguments.Positionals is [var only] ? (null, only) : (arguments.Positionals[0], arguments.Positionals[1]);

        var maxDepth = ParseDepth(arguments.Option("depth"));
        var kinds = ParseKinds(arguments.Option("kinds"));
        var session = await Session.OpenAsync(
            input, arguments.Option("config"), arguments.Option("exclude"), requireConfig: false, cancellationToken);
        var graph = await session.LoadGraphAsync(cancellationToken);
        var target = FindTarget(graph, typeQuery);
        var nodes = graph.Nodes.ToDictionary(n => n.Id);

        var impacted = ImpactAnalyzer.FindDependents(graph, target.Id, maxDepth, kinds);

        Console.WriteLine();
        if (impacted.Count == 0)
        {
            Console.WriteLine($"Nothing in the solution depends on {target.FullName}.");
            return ExitCodes.Success;
        }

        var projects = impacted
            .GroupBy(i => nodes[i.NodeId].Project)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Count()})");
        Console.WriteLine($"Changing {target.FullName} may affect {impacted.Count} type(s) in: {string.Join(", ", projects)}");

        foreach (var distance in impacted.GroupBy(i => i.Distance))
        {
            Console.WriteLine();
            Console.WriteLine(Ansi.Bold(distance.Key == 1 ? "Direct dependents" : $"{distance.Key} steps away"));
            foreach (var type in distance)
            {
                var node = nodes[type.NodeId];
                Console.WriteLine($"  {node.FullName}  {Display.Location(type.Path[0].Location)}");
                Console.WriteLine($"    via {DescribePath(type.Path, nodes)}");
            }
        }

        return ExitCodes.Success;
    }

    private static CodeNode FindTarget(DependencyGraph graph, string query)
    {
        var matches = graph.FindTypes(query);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new UsageException($"No type matches '{query}'.", showUsage: false),
            // Full names can repeat across projects (e.g. every project's DependencyInjection class in
            // Microsoft.Extensions.DependencyInjection), so offer the id, which is always unique.
            _ => throw new UsageException(
                $"'{query}' matches {matches.Count} types. Use a full name, or the id if full names repeat:{Environment.NewLine}" +
                string.Join(Environment.NewLine, matches.Select(m => $"  {m.FullName}  [{m.Project}]  id: {m.Id}")),
                showUsage: false),
        };
    }

    /// <summary>OrdersController -[ConstructorParameter]-> OrderService -[Field]-> IOrderRepository</summary>
    private static string DescribePath(IReadOnlyList<CodeEdge> path, Dictionary<string, CodeNode> nodes) =>
        nodes[path[0].SourceId].Name + string.Concat(path.Select(e => $" -[{e.Kind}]-> {nodes[e.TargetId].Name}"));

    private static int? ParseDepth(string? value) => value switch
    {
        null => null,
        _ when int.TryParse(value, out var depth) && depth > 0 => depth,
        _ => throw new UsageException($"--depth must be a positive number, not '{value}'."),
    };

    private static HashSet<DependencyKind>? ParseKinds(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var kinds = new HashSet<DependencyKind>();
        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<DependencyKind>(name, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                throw new UsageException($"Unknown dependency kind '{name}'.");
            }

            kinds.Add(kind);
        }

        return kinds;
    }
}
