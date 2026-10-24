using CodeAtlas.Analysis;
using CodeAtlas.Core;
using CodeAtlas.Core.Rules;

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

/// <summary>Positional arguments, <c>--name value</c> options, and value-less <c>--flag</c>s.</summary>
internal sealed record CliArguments(
    IReadOnlyList<string> Positionals,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlySet<string> Flags)
{
    /// <param name="aliases">Maps each accepted spelling (<c>-o</c>, <c>--output</c>) to the option's name.</param>
    /// <param name="flags">Options that take no value, such as <c>--force</c>.</param>
    public static CliArguments Parse(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> aliases,
        params string[] flags)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var setFlags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith('-'))
            {
                positionals.Add(args[i]);
                continue;
            }

            if (flags.Contains(args[i]))
            {
                setFlags.Add(args[i].TrimStart('-'));
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

        return new CliArguments(positionals, options, setFlags);
    }

    public string? Option(string name) => Options.GetValueOrDefault(name);

    public bool Flag(string name) => Flags.Contains(name);

    /// <summary>Checks the positional count; <paramref name="optional"/> leading arguments may be left out.</summary>
    public void ExpectPositionals(int optional, params string[] names)
    {
        if (Positionals.Count < names.Length - optional || Positionals.Count > names.Length)
        {
            var shown = names.Select((n, i) => i < optional ? $"[{n}]" : $"<{n}>");
            throw new UsageException($"Expected {string.Join(" ", shown)}.");
        }
    }
}

/// <summary>
/// What a command works on: the resolved input (a solution, project, or saved graph.json), the
/// <c>codeatlas.json</c> that applies to it (if any), and which projects to leave out.
/// </summary>
internal sealed record Session(string Input, ArchitectureConfig? Config, string? ConfigPath, ProjectFilter Filter)
{
    public const string ConfigFileName = ConfigLocator.FileName;

    public bool IsSavedGraph => Input.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public string InputDirectory => Path.GetDirectoryName(Input) ?? ".";

    /// <param name="input">A solution, project, graph.json, folder, or null for the current folder.</param>
    /// <param name="configOption">An explicit <c>--config</c> path; otherwise codeatlas.json is looked up from the input's folder.</param>
    /// <param name="excludeOption">Comma-separated <c>--exclude</c> patterns, added to the config's.</param>
    /// <param name="requireConfig">Fail with a hint to run <c>codeatlas init</c> when no config is found.</param>
    public static async Task<Session> OpenAsync(
        string? input,
        string? configOption,
        string? excludeOption,
        bool requireConfig,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveInput(input);
        var inputDirectory = Path.GetDirectoryName(resolved) ?? ".";

        var configPath = configOption is not null ? Path.GetFullPath(configOption) : ConfigLocator.Find(inputDirectory);
        if (configOption is not null && !File.Exists(configPath))
        {
            throw new UsageException($"Config file not found: {configOption}", showUsage: false);
        }

        if (configPath is null && requireConfig)
        {
            throw new UsageException(
                $"No {ConfigFileName} found in {inputDirectory} or its parent folders. " +
                "Run `codeatlas init` to generate one for this solution, or pass --config.",
                showUsage: false);
        }

        var config = configPath is null ? null : await LoadConfigAsync(configPath, cancellationToken);
        var patterns = (config?.Exclude ?? []).Concat(SplitList(excludeOption));
        return new Session(resolved, config, configPath, new ProjectFilter(patterns));
    }

    /// <summary>Loads the saved graph, or analyzes the solution. Status and warnings go to stderr.</summary>
    public async Task<DependencyGraph> LoadGraphAsync(CancellationToken cancellationToken)
    {
        if (ConfigPath is not null)
        {
            Console.Error.WriteLine(Ansi.Dim($"Using {Path.GetRelativePath(Environment.CurrentDirectory, ConfigPath)}"));
        }

        if (IsSavedGraph)
        {
            return Filter.Apply(GraphJson.Deserialize(await File.ReadAllTextAsync(Input, cancellationToken)));
        }

        Console.Error.WriteLine($"Loading {Path.GetRelativePath(Environment.CurrentDirectory, Input)}...");
        var graph = await SolutionAnalyzer.AnalyzeAsync(Input, new ConsoleProgress(), cancellationToken, Filter);

        if (graph.Warnings.Count > 0)
        {
            Console.Error.WriteLine(Ansi.Yellow($"Warnings ({graph.Warnings.Count}); the graph may be incomplete:"));
            foreach (var warning in graph.Warnings)
            {
                Console.Error.WriteLine($"  - {warning}");
            }
        }

        return graph;
    }

    public static async Task<ArchitectureConfig> LoadConfigAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return ArchitectureConfig.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (ArchitectureConfigException ex)
        {
            throw new UsageException($"Invalid config {path}: {ex.Message}", showUsage: false);
        }
    }

    private static string ResolveInput(string? input)
    {
        // @name: a repo saved with `codeatlas repos add` or the desktop app.
        if (input is not null && input.StartsWith('@'))
        {
            var saved = Repos.RepoRegistry.Load().Find(input)
                ?? throw new UsageException($"No saved repo named '{input}'. Run `codeatlas repos` to list them.", showUsage: false);
            input = saved.Path;
        }

        if (input is not null && input.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(input)
                ? Path.GetFullPath(input)
                : throw new UsageException($"File not found: {input}", showUsage: false);
        }

        try
        {
            return SolutionLocator.Resolve(input);
        }
        catch (SolutionNotFoundException ex)
        {
            throw new UsageException(ex.Message, showUsage: false);
        }
    }

    private static IEnumerable<string> SplitList(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

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
