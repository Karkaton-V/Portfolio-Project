using System.Reflection;
using CodeAtlas.Core;
using CodeAtlas.Core.Rules;

namespace CodeAtlas.Cli.Commands;

internal static class CheckCommand
{
    private const string DefaultConfigFileName = "codeatlas.json";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>
        {
            ["--config"] = "config",
            ["--sarif"] = "sarif",
            ["--path-prefix"] = "path-prefix",
        });
        arguments.ExpectPositionals("input");

        var inputPath = Path.GetFullPath(arguments.Positionals[0]);
        var inputDirectory = Path.GetDirectoryName(inputPath) ?? ".";

        // Read the config before loading the solution, so a typo fails in milliseconds, not minutes.
        var config = await LoadConfigAsync(
            arguments.Option("config") ?? Path.Combine(inputDirectory, DefaultConfigFileName), cancellationToken);
        var graph = await GraphInput.LoadAsync(arguments.Positionals[0], cancellationToken);
        var result = ArchitectureChecker.Check(graph, config);

        PrintReport(graph, result);

        if (arguments.Option("sarif") is { } sarifPath)
        {
            var pathPrefix = arguments.Option("path-prefix") ?? RepositoryRelativePrefix(inputPath);
            await File.WriteAllTextAsync(sarifPath, SarifWriter.Write(result, ToolVersion(), pathPrefix), cancellationToken);
            Console.WriteLine($"Wrote SARIF to {Path.GetFullPath(sarifPath)}");
        }

        return result.ErrorCount > 0 ? ExitCodes.RuleViolations : ExitCodes.Success;
    }

    private static async Task<ArchitectureConfig> LoadConfigAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new UsageException(
                $"Config file not found: {path}. Create {DefaultConfigFileName} next to the input or pass --config.",
                showUsage: false);
        }

        try
        {
            return ArchitectureConfig.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (ArchitectureConfigException ex)
        {
            throw new UsageException($"Invalid config {path}: {ex.Message}", showUsage: false);
        }
    }

    private static void PrintReport(DependencyGraph graph, CheckResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"Checked {result.Rules.Count} rule(s) against {graph.Nodes.Count} types and {graph.Edges.Count} dependencies.");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"Config warning: {warning}");
        }

        var violationsByRule = result.Violations.ToLookup(v => v.RuleId);
        foreach (var rule in result.Rules)
        {
            var violations = violationsByRule[rule.Id].ToList();
            Console.WriteLine();

            if (violations.Count == 0)
            {
                Console.WriteLine($"[PASS] {rule.Name}");
                continue;
            }

            var status = rule.Severity == RuleSeverity.Error ? "[FAIL]" : "[WARN]";
            Console.WriteLine($"{status} {rule.Name} ({violations.Count} violation(s))");
            Console.WriteLine($"       {rule.Description}");
            foreach (var violation in violations)
            {
                Console.WriteLine($"   {violation.Message}");
                if (violation.Location is not null)
                {
                    Console.WriteLine($"     at {Display.Location(violation.Location)}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(result.ErrorCount > 0
            ? $"Check failed: {result.ErrorCount} error(s), {result.WarningCount} warning(s)."
            : $"Check passed with {result.WarningCount} warning(s).");
    }

    /// <summary>
    /// SARIF paths must be relative to the repository root, but graph paths are relative to the solution.
    /// When the solution sits in a subfolder of a Git repository, returns that subfolder (e.g. "samples/Shop").
    /// A saved graph.json doesn't record where its solution was, so --path-prefix is needed then.
    /// </summary>
    private static string? RepositoryRelativePrefix(string inputPath)
    {
        if (Path.GetExtension(inputPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var solutionDirectory = Path.GetDirectoryName(inputPath);
        for (var directory = solutionDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            // .git is a directory in a normal clone and a file in a worktree or submodule.
            var gitPath = Path.Combine(directory, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                var relative = Path.GetRelativePath(directory, solutionDirectory ?? directory).Replace('\\', '/');
                return relative == "." ? null : relative;
            }
        }

        return null;
    }

    private static string ToolVersion()
    {
        var version = typeof(CheckCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var metadata = version.IndexOf('+'); // "0.3.0+<commit sha>"
        return metadata < 0 ? version : version[..metadata];
    }
}
