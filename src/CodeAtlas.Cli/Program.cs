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
        ["analyze", .. var rest] => await AnalyzeCommand.RunAsync(rest, cancellation.Token),
        ["cycles", .. var rest] => await CyclesCommand.RunAsync(rest, cancellation.Token),
        ["impact", .. var rest] => await ImpactCommand.RunAsync(rest, cancellation.Token),
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
          codeatlas analyze <solution> [-o <graph.json>]
          codeatlas cycles  <input> [--level namespace|type]
          codeatlas impact  <input> <type> [--depth <n>] [--kinds <kind,...>]

        <solution> is a .sln, .slnx, or .csproj file. <input> is a solution or a graph.json written by
        `analyze -o`; reusing a saved graph skips the slow solution load.

        Commands:
          analyze    Extract types and dependencies; optionally save them as JSON
          cycles     Find groups of namespaces (or types) that depend on each other in a loop
          impact     List the types that depend on <type>, directly or indirectly

        Dependency kinds:
          Inherits, Implements, ConstructorParameter, Field, Property, MethodSignature,
          Instantiates, Calls, References
        """);
    return exitCode;
}
