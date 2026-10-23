using CodeAtlas.Analysis;
using CodeAtlas.Core;

const int Success = 0;
const int UsageError = 1;
const int AnalysisError = 2;
const int Cancelled = 130;

if (args is ["-h" or "--help" or "help", ..])
{
    PrintUsage(Console.Out);
    return Success;
}

if (args is not ["analyze", var inputPath, .. var options])
{
    PrintUsage(Console.Error);
    return UsageError;
}

string? outputPath = null;
for (var i = 0; i < options.Length; i++)
{
    switch (options[i])
    {
        case "-o" or "--output" when i + 1 < options.Length:
            outputPath = options[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete option '{options[i]}'.");
            PrintUsage(Console.Error);
            return UsageError;
    }
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"File not found: {inputPath}");
    return UsageError;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

DependencyGraph graph;
try
{
    Console.Error.WriteLine($"Loading {inputPath}...");
    graph = await SolutionAnalyzer.AnalyzeAsync(inputPath, new ConsoleProgress(), cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return Cancelled;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Analysis failed: {ex.Message}");
    return AnalysisError;
}

PrintSummary(graph);

if (outputPath is not null)
{
    await File.WriteAllTextAsync(outputPath, GraphJson.Serialize(graph));
    Console.WriteLine($"Wrote graph to {Path.GetFullPath(outputPath)}");
}

return Success;

static void PrintSummary(DependencyGraph graph)
{
    var projectCount = graph.Nodes.Select(n => n.Project).Distinct().Count();
    Console.WriteLine();
    Console.WriteLine($"{graph.Nodes.Count} types and {graph.Edges.Count} dependencies across {projectCount} project(s)");

    foreach (var group in graph.Edges.GroupBy(e => e.Kind).OrderBy(g => g.Key))
    {
        Console.WriteLine($"  {group.Key,-22}{group.Count(),6}");
    }

    if (graph.Warnings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Warnings ({graph.Warnings.Count}); the graph may be incomplete:");
        foreach (var warning in graph.Warnings)
        {
            Console.WriteLine($"  - {warning}");
        }
    }
}

static void PrintUsage(TextWriter writer) => writer.WriteLine(
    """

    Usage:
      codeatlas analyze <path> [-o|--output <file>]

    Arguments:
      <path>    A .sln, .slnx, or .csproj file to analyze

    Options:
      -o, --output <file>    Write the dependency graph as JSON to <file>
    """);

internal sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string projectName) => Console.Error.WriteLine($"  Analyzing {projectName}");
}
