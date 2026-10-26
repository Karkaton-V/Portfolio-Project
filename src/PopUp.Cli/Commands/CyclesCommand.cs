using PopUp.Core.Algorithms;

namespace PopUp.Cli.Commands;

internal static class CyclesCommand
{
    private const int MaxEvidenceLines = 10;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>
        {
            ["--level"] = "level",
            ["--config"] = "config",
            ["--exclude"] = "exclude",
        });
        arguments.ExpectPositionals(1, "input");

        var level = arguments.Option("level") switch
        {
            null or "namespace" => CycleLevel.Namespace,
            "type" => CycleLevel.Type,
            var other => throw new UsageException($"Unknown level '{other}'. Use 'namespace' or 'type'."),
        };

        var session = await Session.OpenAsync(
            arguments.Positionals.FirstOrDefault(), arguments.Option("config"), arguments.Option("exclude"), requireConfig: false, cancellationToken);
        var graph = await session.LoadGraphAsync(cancellationToken);
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        var cycles = CycleDetector.FindCycles(graph, level);
        var levelName = level == CycleLevel.Namespace ? "namespace" : "type";

        string MemberName(string member) =>
            level == CycleLevel.Namespace ? Display.Namespace(member) : nodes[member].FullName;

        Console.WriteLine();
        if (cycles.Count == 0)
        {
            Console.WriteLine($"No {levelName} cycles found.");
            return ExitCodes.Success;
        }

        Console.WriteLine(Ansi.Red($"Found {cycles.Count} {levelName} cycle(s)."));
        for (var i = 0; i < cycles.Count; i++)
        {
            var cycle = cycles[i];
            Console.WriteLine();
            Console.WriteLine(Ansi.Bold($"Cycle {i + 1}: {cycle.Members.Count} {levelName}s"));
            foreach (var member in cycle.Members)
            {
                Console.WriteLine($"  {MemberName(member)}");
            }

            Console.WriteLine($"  Loop: {string.Join(" -> ", cycle.ExamplePath.Select(MemberName))}");
            Console.WriteLine("  Caused by:");
            foreach (var edge in cycle.Evidence.Take(MaxEvidenceLines))
            {
                Console.WriteLine(
                    $"    {nodes[edge.SourceId].FullName} -[{edge.Kind}]-> {nodes[edge.TargetId].FullName}  {Display.Location(edge.Location)}");
            }

            if (cycle.Evidence.Count > MaxEvidenceLines)
            {
                Console.WriteLine($"    ...and {cycle.Evidence.Count - MaxEvidenceLines} more");
            }
        }

        return ExitCodes.Success;
    }
}
