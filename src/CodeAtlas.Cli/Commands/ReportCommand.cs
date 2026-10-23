using CodeAtlas.Core.Rules;
using CodeAtlas.Report;

namespace CodeAtlas.Cli.Commands;

internal static class ReportCommand
{
    private const string DefaultOutput = "codeatlas-report.html";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>
        {
            ["-o"] = "output",
            ["--output"] = "output",
            ["--config"] = "config",
            ["--title"] = "title",
        });
        arguments.ExpectPositionals("input");

        var inputPath = Path.GetFullPath(arguments.Positionals[0]);

        // Rule results are optional in a report: use --config, or codeatlas.json next to the input if there is one.
        var configPath = arguments.Option("config")
            ?? Path.Combine(Path.GetDirectoryName(inputPath) ?? ".", CheckCommand.DefaultConfigFileName);
        var config = arguments.Option("config") is not null || File.Exists(configPath)
            ? await CheckCommand.LoadConfigAsync(configPath, cancellationToken)
            : null;

        var graph = await GraphInput.LoadAsync(arguments.Positionals[0], cancellationToken);

        var html = HtmlReportWriter.Write(graph, new ReportOptions
        {
            Title = arguments.Option("title") ?? Path.GetFileNameWithoutExtension(inputPath),
            ToolVersion = CheckCommand.ToolVersion(),
            Check = config is null ? null : ArchitectureChecker.Check(graph, config),
        });

        var outputPath = Path.GetFullPath(arguments.Option("output") ?? DefaultOutput);
        await File.WriteAllTextAsync(outputPath, html, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Wrote report for {graph.Nodes.Count} types to {outputPath}");
        if (config is not null)
        {
            Console.WriteLine($"Included architecture rules from {configPath}");
        }

        return ExitCodes.Success;
    }
}
