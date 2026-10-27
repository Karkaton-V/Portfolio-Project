using PopUp.Cli;
using PopUp.Cli.Commands;

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
        ["repos", .. var rest] => await ReposCommand.RunAsync(rest, cancellation.Token),
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
          popup init    [input] [--force]
          popup analyze [input] [-o <graph.json>]
          popup cycles  [input] [--level namespace|type]
          popup impact  [input] <type> [--depth <n>] [--kinds <kind,...>]
          popup check   [input] [--sarif <file>] [--path-prefix <dir>]
          popup report  [input] [-o <report.html>] [--title <text>] [--theme <name>] [--branch <name>]

        Saved repos (shared with the PopUp desktop app):
          popup repos                       List saved repos
          popup repos add <folder|owner/name> [--name <name>] [--no-restore]
          popup repos remove <name>         Forget a repo (files are left in place)
          popup repos pull <name>           Pull the latest commits of a repo added from GitHub
          popup repos github [--all]        List your GitHub repos (C# ones unless --all)

        [input] is a folder (default: the current folder), a .sln, .slnx, or .csproj file, a graph.json
        written by `analyze -o`, or @name for a saved repo. A folder is searched for its solution, including a
        few levels down (e.g. src/); reusing a saved graph skips the slow solution load.

        Every command also takes:
          --config <file>       popup.json to use (default: found in the input's folder or a parent)
          --exclude <globs>     Projects to leave out, comma-separated, e.g. "*Tests,Samples.*"

        Commands:
          init       Generate a starter popup.json, with layers guessed from project names
          analyze    Extract types and dependencies; optionally save them as JSON
          cycles     Find groups of namespaces (or types) that depend on each other in a loop
          impact     List the types that depend on <type>, directly or indirectly
          check      Enforce the architecture rules in popup.json
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
