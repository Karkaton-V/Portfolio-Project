using Burndown.Core;

namespace Burndown.Cli.Commands;

internal static class AnalyzeCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>
        {
            ["-o"] = "output",
            ["--output"] = "output",
            ["--config"] = "config",
            ["--exclude"] = "exclude",
        });
        arguments.ExpectPositionals(1, "input");

        var session = await Session.OpenAsync(
            arguments.Positionals.FirstOrDefault(), arguments.Option("config"), arguments.Option("exclude"), requireConfig: false, cancellationToken);
        var graph = await session.LoadGraphAsync(cancellationToken);

        var projectCount = graph.Nodes.Select(n => n.Project).Distinct().Count();
        Console.WriteLine();
        Console.WriteLine($"{graph.Nodes.Count} types and {graph.Edges.Count} dependencies across {projectCount} project(s)");
        foreach (var group in graph.Edges.GroupBy(e => e.Kind).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Key,-22}{group.Count(),6}");
        }

        if (arguments.Option("output") is { } outputPath)
        {
            await File.WriteAllTextAsync(outputPath, GraphJson.Serialize(graph), cancellationToken);
            Console.WriteLine($"Wrote graph to {Path.GetFullPath(outputPath)}");
        }

        return ExitCodes.Success;
    }
}
