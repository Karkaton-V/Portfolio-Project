using PopUp.Core;
using PopUp.Core.Rules;
using PopUp.Report;

namespace PopUp.Cli.Commands;

internal static class ReportCommand
{
    private const string DefaultOutput = "popup-report.html";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>
        {
            ["-o"] = "output",
            ["--output"] = "output",
            ["--config"] = "config",
            ["--exclude"] = "exclude",
            ["--title"] = "title",
            ["--theme"] = "theme",
        });
        arguments.ExpectPositionals(1, "input");

        if (arguments.Option("theme") is { } requested && !ReportThemes.IsKnown(requested))
        {
            throw new UsageException($"Unknown theme '{requested}'. Choose one of: {string.Join(", ", ReportThemes.Names)}.", showUsage: false);
        }

        // Rule results are optional in a report: they're included when a popup.json applies.
        var session = await Session.OpenAsync(
            arguments.Positionals.FirstOrDefault(), arguments.Option("config"), arguments.Option("exclude"), requireConfig: false, cancellationToken);
        var graph = await session.LoadGraphAsync(cancellationToken);
        var settings = session.Config?.Report ?? new ReportSettings();

        var html = HtmlReportWriter.Write(graph, new ReportOptions
        {
            Title = arguments.Option("title") ?? settings.Title ?? Path.GetFileNameWithoutExtension(session.Input),
            Theme = (arguments.Option("theme") ?? settings.Theme ?? ReportThemes.Default).ToLowerInvariant(),
            ToolVersion = CheckCommand.ToolVersion(),
            Check = session.Config is null ? null : ArchitectureChecker.Check(graph, session.Config),
        });

        var outputPath = Path.GetFullPath(arguments.Option("output") ?? DefaultOutput);
        await File.WriteAllTextAsync(outputPath, html, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Wrote report for {graph.Nodes.Count} types to {outputPath}");
        if (session.ConfigPath is not null)
        {
            Console.WriteLine(Ansi.Dim($"Included architecture rules from {session.ConfigPath}"));
        }

        return ExitCodes.Success;
    }
}
