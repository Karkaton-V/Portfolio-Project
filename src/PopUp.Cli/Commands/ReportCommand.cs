using PopUp.Core;
using PopUp.Core.Rules;
using PopUp.Report;
using PopUp.Repos;

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

        // The header reads "owner / title" for a clone of a GitHub repository, "parent folder / title" otherwise.
        // A report made from a saved graph.json has no folder to describe.
        var input = arguments.Positionals.FirstOrDefault() ?? ".";
        var fromFolder = Directory.Exists(input);
        var location = session.Input.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? null
            : LocalRepoFinder.Describe(fromFolder ? input : session.InputDirectory);

        var html = HtmlReportWriter.Write(graph, new ReportOptions
        {
            // Run on a folder, the title is the folder (or repository) name, like the desktop app; run on a
            // solution or project file, it's that file's name.
            Title = arguments.Option("title") ?? settings.Title
                ?? (fromFolder && location is not null ? location.Name : Path.GetFileNameWithoutExtension(session.Input)),
            Source = location switch
            {
                null => null,
                { GitHub: { } gitHub } => new ReportSource(ReportSourceKind.GitHub, gitHub.Owner, gitHub.WebUrl),
                _ => new ReportSource(ReportSourceKind.Folder, location.Owner),
            },
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
