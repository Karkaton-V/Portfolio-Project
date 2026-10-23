using CodeAtlas.Analysis;
using CodeAtlas.Core;

namespace CodeAtlas.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int UsageError = 1;
    public const int Failure = 2;
    public const int RuleViolations = 3;
    public const int Cancelled = 130;
}

/// <summary>Bad command-line input. The message is shown to the user, optionally followed by the usage text.</summary>
/// <param name="showUsage">False when the command was well-formed but refers to something that doesn't exist.</param>
internal sealed class UsageException(string message, bool showUsage = true) : Exception(message)
{
    public bool ShowUsage { get; } = showUsage;
}

/// <summary>Positional arguments plus <c>--name value</c> options. Every option takes a value.</summary>
internal sealed record CliArguments(IReadOnlyList<string> Positionals, IReadOnlyDictionary<string, string> Options)
{
    /// <param name="aliases">Maps each accepted spelling (<c>-o</c>, <c>--output</c>) to the option's name.</param>
    public static CliArguments Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> aliases)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith('-'))
            {
                positionals.Add(args[i]);
                continue;
            }

            if (!aliases.TryGetValue(args[i], out var name))
            {
                throw new UsageException($"Unknown option '{args[i]}'.");
            }

            if (i + 1 >= args.Count)
            {
                throw new UsageException($"Option '{args[i]}' needs a value.");
            }

            options[name] = args[++i];
        }

        return new CliArguments(positionals, options);
    }

    public string? Option(string name) => Options.GetValueOrDefault(name);

    public void ExpectPositionals(params string[] names)
    {
        if (Positionals.Count != names.Length)
        {
            throw new UsageException($"Expected {string.Join(" ", names.Select(n => $"<{n}>"))}.");
        }
    }
}

internal static class GraphInput
{
    /// <summary>Loads a saved graph.json, or analyzes a solution/project. Warnings are written to stderr.</summary>
    public static async Task<DependencyGraph> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new UsageException($"File not found: {path}", showUsage: false);
        }

        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return GraphJson.Deserialize(await File.ReadAllTextAsync(path, cancellationToken));
        }

        Console.Error.WriteLine($"Loading {path}...");
        var graph = await SolutionAnalyzer.AnalyzeAsync(path, new ConsoleProgress(), cancellationToken);

        if (graph.Warnings.Count > 0)
        {
            Console.Error.WriteLine($"Warnings ({graph.Warnings.Count}); the graph may be incomplete:");
            foreach (var warning in graph.Warnings)
            {
                Console.Error.WriteLine($"  - {warning}");
            }
        }

        return graph;
    }

    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string message) => Console.Error.WriteLine($"  {message}");
    }
}

internal static class Display
{
    public static string Location(SourceLocation? location) =>
        location is null ? "" : Ansi.Dim($"{location.FilePath}:{location.Line}");

    public static string Namespace(string name) => name.Length == 0 ? "(global namespace)" : name;
}
