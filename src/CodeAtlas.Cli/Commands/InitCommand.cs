using CodeAtlas.Core.Rules;

namespace CodeAtlas.Cli.Commands;

/// <summary>
/// <c>codeatlas init</c>: generates a starter codeatlas.json for any solution, with layers guessed from project
/// names, and reports how the solution measures up against it right away.
/// </summary>
internal static class InitCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>(), "--force");
        arguments.ExpectPositionals(1, "input");

        var session = await Session.OpenAsync(
            arguments.Positionals.FirstOrDefault(), configOption: null, excludeOption: null, requireConfig: false, cancellationToken);
        var target = Path.Combine(session.InputDirectory, Session.ConfigFileName);

        if (File.Exists(target) && !arguments.Flag("force"))
        {
            throw new UsageException($"{target} already exists. Pass --force to replace it.", showUsage: false);
        }

        // Analyze everything (including tests), ignoring any existing config's exclusions: init needs to see
        // every project to sort them into layers.
        var graph = await (session with { ConfigPath = null, Filter = Core.ProjectFilter.None }).LoadGraphAsync(cancellationToken);
        var generated = ConfigGenerator.Generate(graph, Path.GetFileNameWithoutExtension(session.Input));
        await File.WriteAllTextAsync(target, generated.Json, cancellationToken);

        Console.WriteLine();
        Console.WriteLine(Ansi.Green($"Wrote {Path.GetRelativePath(Environment.CurrentDirectory, target)}"));
        if (generated.Layers.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(Ansi.Bold("Layers (guessed from project names; review them):"));
            foreach (var (layer, projects) in generated.Layers)
            {
                Console.WriteLine($"  {layer,-16}{string.Join(", ", projects)}");
            }
        }

        if (generated.Unassigned.Count > 0)
        {
            Console.WriteLine($"  {Ansi.Yellow("Not assigned")}    {string.Join(", ", generated.Unassigned)}");
        }

        // Check the solution against the new rules once, so the first run shows where things stand.
        var result = ArchitectureChecker.Check(graph, ArchitectureConfig.Parse(generated.Json));
        Console.WriteLine();
        Console.WriteLine($"{generated.RuleCount} layering rule(s) plus a namespace cycle check. Against the current code: " +
            (result.ErrorCount == 0 && result.WarningCount == 0
                ? Ansi.Green("everything passes.")
                : $"{Ansi.Red($"{result.ErrorCount} error(s)")}, {Ansi.Yellow($"{result.WarningCount} warning(s)")}."));
        Console.WriteLine("Next: review the layers, then run `codeatlas check` for details or `codeatlas report` to explore.");

        return ExitCodes.Success;
    }
}
