using System.Reflection;
using PopUp.Core;
using PopUp.Core.Rules;

namespace PopUp.Cli.Commands;

internal static class CheckCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>
        {
            ["--config"] = "config",
            ["--exclude"] = "exclude",
            ["--sarif"] = "sarif",
            ["--path-prefix"] = "path-prefix",
        });
        arguments.ExpectPositionals(1, "input");

        // The config is read before the solution is loaded, so a typo fails in milliseconds, not minutes.
        var session = await Session.OpenAsync(
            arguments.Positionals.FirstOrDefault(), arguments.Option("config"), arguments.Option("exclude"), requireConfig: true, cancellationToken);
        var config = session.Config ?? throw new InvalidOperationException("A config is required here.");
        var inputPath = session.Input;
        var graph = await session.LoadGraphAsync(cancellationToken);
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

    private static void PrintReport(DependencyGraph graph, CheckResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"Checked {result.Rules.Count} rule(s) against {graph.Nodes.Count} types and {graph.Edges.Count} dependencies.");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"{Ansi.Yellow("Config warning:")} {warning}");
        }

        var violationsByRule = result.Violations.ToLookup(v => v.RuleId);
        foreach (var rule in result.Rules)
        {
            var violations = violationsByRule[rule.Id].ToList();
            Console.WriteLine();

            if (violations.Count == 0)
            {
                Console.WriteLine($"{Ansi.Green("[PASS]")} {rule.Name}");
                continue;
            }

            var status = rule.Severity == RuleSeverity.Error ? Ansi.Red("[FAIL]") : Ansi.Yellow("[WARN]");
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
            ? Ansi.Red($"Check failed: {result.ErrorCount} error(s), {result.WarningCount} warning(s).")
            : Ansi.Green($"Check passed with {result.WarningCount} warning(s)."));
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

    internal static string ToolVersion()
    {
        var version = typeof(CheckCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var metadata = version.IndexOf('+'); // "0.3.0+<commit sha>"
        return metadata < 0 ? version : version[..metadata];
    }
}
