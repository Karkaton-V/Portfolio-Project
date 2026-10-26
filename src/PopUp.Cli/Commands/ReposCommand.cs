using PopUp.Analysis;
using PopUp.Repos;

namespace PopUp.Cli.Commands;

/// <summary>
/// <c>popup repos</c>: the saved repositories shared with the desktop app. A saved repository can be used
/// as the input of any command with <c>@name</c>, e.g. <c>popup check @shop</c>.
/// </summary>
internal static class ReposCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var registry = RepoRegistry.Load();
        return args.ToArray() switch // an array, so `.. var rest` slicing works
        {
            [] or ["list"] => List(registry),
            ["add", .. var rest] => await AddAsync(registry, rest, cancellationToken),
            ["remove", .. var rest] => Remove(registry, rest),
            ["locate", .. var rest] => Locate(registry, rest),
            ["pull", .. var rest] => await PullAsync(registry, rest, cancellationToken),
            ["github", .. var rest] => await GitHubAsync(registry, rest, cancellationToken),
            [var other, ..] => throw new UsageException($"Unknown repos command '{other}'. Use list, add, remove, locate, pull, or github."),
        };
    }

    private static int List(RepoRegistry registry)
    {
        if (registry.Repos.Count == 0)
        {
            Console.WriteLine("No saved repos yet. Add one with `popup repos add <folder or owner/name>`.");
            return ExitCodes.Success;
        }

        var width = registry.Repos.Max(r => r.Name.Length) + 2;
        foreach (var repo in registry.Repos)
        {
            var missing = Directory.Exists(repo.Path) ? "" : Ansi.Red("  (folder missing)");
            Console.WriteLine($"{Ansi.Bold(("@" + repo.Name).PadRight(width + 1))}{repo.GitHub ?? repo.Path}{missing}");
        }

        Console.WriteLine();
        Console.WriteLine(Ansi.Dim($"Use one with any command, e.g. `popup report @{registry.Repos[0].Name}`. Saved in {registry.Home}."));
        return ExitCodes.Success;
    }

    private static async Task<int> AddAsync(RepoRegistry registry, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string> { ["--name"] = "name" }, "--no-restore", "--clone");
        arguments.ExpectPositionals(0, "folder or owner/name");
        var source = arguments.Positionals[0];

        RegisteredRepo repo;
        GitHubRepoRef? requested = null;
        if (!Directory.Exists(source)
            && GitHubRepoRef.TryParse(source, out requested)
            && !arguments.Flag("clone")
            && LocalRepoFinder.Find().GetValueOrDefault(requested.FullName) is [var existing, ..])
        {
            // The user already has a clone (e.g. from GitHub Desktop): use it instead of downloading another.
            Resolve(existing);
            Console.Error.WriteLine($"Using your existing copy at {existing} (pass --clone to download a separate one).");
            repo = new RegisteredRepo(
                arguments.Option("name") ?? registry.SuggestName(requested.Name),
                existing, requested.FullName, Managed: false, AddedAt: DateTimeOffset.Now);
        }
        else if (Directory.Exists(source))
        {
            var folder = Path.GetFullPath(source);
            Resolve(folder);
            repo = new RegisteredRepo(
                arguments.Option("name") ?? registry.SuggestName(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))),
                folder, LocalRepoFinder.ReadOrigin(folder)?.FullName, Managed: false, AddedAt: DateTimeOffset.Now);
        }
        else if (GitHubRepoRef.TryParse(source, out var gitHub))
        {
            var destination = registry.CloneDirectoryFor(gitHub);
            var cloned = false;
            if (!Directory.Exists(Path.Combine(destination, ".git")))
            {
                Console.Error.WriteLine($"Cloning {gitHub.FullName} into {destination}...");
                await Tool(() => Tools.CloneAsync(gitHub, destination, cancellationToken));
                cloned = true;
            }

            string solution;
            try
            {
                solution = Resolve(destination);
            }
            catch (UsageException) when (cloned)
            {
                // Nothing to analyze (e.g. a Unity project, which doesn't commit .sln files): remove the download.
                Tools.DeleteClone(destination);
                throw;
            }
            if (!arguments.Flag("no-restore"))
            {
                Console.Error.WriteLine("Restoring NuGet packages (this runs the repository's build scripts)...");
                await Tool(() => Tools.RestoreAsync(solution, cancellationToken));
            }

            repo = new RegisteredRepo(
                arguments.Option("name") ?? registry.SuggestName(gitHub.Name),
                destination, gitHub.FullName, Managed: true, AddedAt: DateTimeOffset.Now);
        }
        else
        {
            throw new UsageException($"'{source}' isn't a folder or a GitHub repository (owner/name or URL).", showUsage: false);
        }

        try
        {
            registry.Add(repo);
        }
        catch (RepoRegistryException ex)
        {
            throw new UsageException(ex.Message, showUsage: false);
        }

        registry.Save();
        Console.WriteLine(Ansi.Green($"Saved @{repo.Name}") + $" ({repo.Path})");
        return ExitCodes.Success;
    }

    private static int Remove(RepoRegistry registry, IReadOnlyList<string> args)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>());
        arguments.ExpectPositionals(0, "name");
        var name = arguments.Positionals[0];
        if (!registry.Remove(name))
        {
            throw new UsageException($"No saved repo named '{name}'. Run `popup repos` to list them.", showUsage: false);
        }

        registry.Save();
        Console.WriteLine($"Removed @{name.TrimStart('@')} (its files were left in place).");
        return ExitCodes.Success;
    }

    /// <summary>Points a saved repo at a folder: where it already lives on this computer.</summary>
    private static int Locate(RepoRegistry registry, IReadOnlyList<string> args)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>(), "--force");
        arguments.ExpectPositionals(0, "name", "folder");
        var repo = registry.Find(arguments.Positionals[0])
            ?? throw new UsageException($"No saved repo named '{arguments.Positionals[0]}'.", showUsage: false);
        var folder = Path.GetFullPath(arguments.Positionals[1]);
        if (!Directory.Exists(folder))
        {
            throw new UsageException($"{folder} doesn't exist.", showUsage: false);
        }

        Resolve(folder);
        var origin = LocalRepoFinder.ReadOrigin(folder);
        if (repo.GitHub is not null && !string.Equals(origin?.FullName, repo.GitHub, StringComparison.OrdinalIgnoreCase) && !arguments.Flag("force"))
        {
            throw new UsageException(
                $"That folder is a clone of {origin?.FullName ?? "nothing on GitHub"}, not {repo.GitHub}. Pass --force to use it anyway.",
                showUsage: false);
        }

        registry.Update(repo with { Path = folder, Managed = false, GitHub = repo.GitHub ?? origin?.FullName });
        registry.Save();
        Console.WriteLine(Ansi.Green($"@{repo.Name} now uses {folder}"));
        return ExitCodes.Success;
    }

    private static async Task<int> PullAsync(RepoRegistry registry, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>());
        arguments.ExpectPositionals(0, "name");
        var repo = registry.Find(arguments.Positionals[0])
            ?? throw new UsageException($"No saved repo named '{arguments.Positionals[0]}'.", showUsage: false);
        if (repo.GitHub is null)
        {
            throw new UsageException($"@{repo.Name} is a local folder; update it with your usual git tools.", showUsage: false);
        }

        await Tool(() => Tools.PullAsync(repo.Path, cancellationToken));
        await Tool(() => Tools.RestoreAsync(Resolve(repo.Path), cancellationToken));
        registry.Update(repo with { SyncedAt = DateTimeOffset.Now });
        registry.Save();
        Console.WriteLine(Ansi.Green($"Updated @{repo.Name} from {repo.GitHub}"));
        return ExitCodes.Success;
    }

    /// <summary>Lists the signed-in user's GitHub repositories (C# ones unless --all), marking those already saved.</summary>
    private static async Task<int> GitHubAsync(RepoRegistry registry, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(args, new Dictionary<string, string>(), "--all");
        var token = await Tools.FindGitHubTokenAsync(cancellationToken)
            ?? throw new UsageException(
                "Not signed in to GitHub. Install the GitHub CLI and run `gh auth login`, or set GITHUB_TOKEN.", showUsage: false);

        using var http = new HttpClient();
        var client = new GitHubClient(http, token);
        IReadOnlyList<GitHubRepo> repos;
        try
        {
            repos = await client.ListMyRepositoriesAsync(cancellationToken);
        }
        catch (GitHubException ex)
        {
            throw new UsageException(ex.Message, showUsage: false);
        }

        var saved = registry.Repos.Select(r => r.GitHub).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = LocalRepoFinder.Find();
        var shown = repos.Where(r => !r.Archived && (arguments.Flag("all") || r.IsCSharp)).ToList();
        if (shown.Count == 0)
        {
            Console.WriteLine(arguments.Flag("all")
                ? "No repositories found for this account."
                : "No repositories with C# as their main language. Use --all to list everything.");
            return ExitCodes.Success;
        }

        var width = shown.Max(r => r.FullName.Length) + 2;
        foreach (var repo in shown)
        {
            var language = repo.Language ?? "-";
            var marker = saved.Contains(repo.FullName) ? Ansi.Green("  saved")
                : local.ContainsKey(repo.FullName) ? Ansi.Yellow("  on this PC")
                : "";
            Console.WriteLine($"{repo.FullName.PadRight(width)}{Ansi.Dim($"{language,-12}{(repo.Private ? "private" : "public")}")}{marker}");
        }

        Console.WriteLine();
        Console.WriteLine(Ansi.Dim("Add one with `popup repos add owner/name`."));
        return ExitCodes.Success;
    }

    private static string Resolve(string folder)
    {
        try
        {
            return SolutionLocator.Resolve(folder);
        }
        catch (SolutionNotFoundException ex)
        {
            throw new UsageException(ex.Message, showUsage: false);
        }
    }

    private static async Task Tool(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ToolException ex)
        {
            throw new UsageException(ex.Message, showUsage: false);
        }
    }
}
