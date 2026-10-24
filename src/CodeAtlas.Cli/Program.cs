using CodeAtlas.Cli;
using CodeAtlas.Cli.Commands;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return args switch
    {
        ["init", .. var rest] => await InitCommand.RunAsync(rest, cancellation.Token),
        ["analyze", .. var rest] => await AnalyzeCommand.RunAsync(rest, cancellation.Token),
        ["cycles", .. var rest] => await CyclesCommand.RunAsync(rest, cancellation.Token),
        ["impact", .. var rest] => await ImpactCommand.RunAsync(rest, cancellation.Token),
        ["check", .. var rest] => await CheckCommand.RunAsync(rest, cancellation.Token),
        ["report", .. var rest] => await ReportCommand.RunAsync(rest, cancellation.Token),
        ["-h" or "--help" or "help", ..] => PrintUsage(Console.Out, ExitCodes.Success),
        _ => PrintUsage(Console.Error, ExitCodes.UsageError),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return ex.ShowUsage ? PrintUsage(Console.Error, ExitCodes.UsageError) : ExitCodes.UsageError;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return ExitCodes.Cancelled;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return ExitCodes.Failure;
}

static int PrintUsage(TextWriter writer, int exitCode)
{
    writer.WriteLine(
        """

        Usage:
          codeatlas init    [input] [--force]
          codeatlas analyze [input] [-o <graph.json>]
          codeatlas cycles  [input] [--level namespace|type]
          codeatlas impact  [input] <type> [--depth <n>] [--kinds <kind,...>]
          codeatlas check   [input] [--sarif <file>] [--path-prefix <dir>]
          codeatlas report  [input] [-o <report.html>] [--title <text>] [--theme <name>]

        [input] is a folder (default: the current folder), a .sln, .slnx, or .csproj file, or a graph.json
        written by `analyze -o`; reusing a saved graph skips the slow solution load. A folder is searched for
        its solution, including a few levels down (e.g. src/).

        Every command also takes:
          --config <file>       codeatlas.json to use (default: found in the input's folder or a parent)
          --exclude <globs>     Projects to leave out, comma-separated, e.g. "*Tests,Samples.*"

        Commands:
          init       Generate a starter codeatlas.json, with layers guessed from project names
          analyze    Extract types and dependencies; optionally save them as JSON
          cycles     Find groups of namespaces (or types) that depend on each other in a loop
          impact     List the types that depend on <type>, directly or indirectly
          check      Enforce the architecture rules in codeatlas.json
          report     Write a self-contained interactive HTML report (opens offline in any browser)

        Report themes:
          atlas (default), nord, solarized, contrast; each has light and dark modes, switchable in the report

        Exit codes:
          0 success   1 bad arguments or config   2 analysis failed   3 rule violations (errors)

        Dependency kinds:
          Inherits, Implements, ConstructorParameter, Field, Property, MethodSignature,
          Instantiates, Calls, References
        """);
    return exitCode;
}
