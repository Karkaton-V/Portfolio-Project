using System.IO;
using PopUp.Repos;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PopUp.Desktop.ViewModels;

/// <summary>One saved repository in the sidebar.</summary>
public sealed class RepoViewModel(RegisteredRepo repo, string reportsDirectory) : ObservableObject
{
    public RegisteredRepo Repo { get; private set; } = repo;

    public string Name => Repo.Name;

    /// <summary>"owner/name" for GitHub repositories, otherwise the folder (shortened; the tooltip has it in full).</summary>
    public string Detail => Repo.GitHub ?? Format.ShortPath(Repo.Path);

    public bool IsGitHub => Repo.GitHub is not null;

    public bool HasLocalCopy => Directory.Exists(Repo.Path);

    /// <summary>The badge: where the code is. A GitHub repository with no local copy is analyzed on GitHub.</summary>
    public string Source => !IsGitHub ? "Folder" : HasLocalCopy ? "GitHub" : "GitHub · cloud";

    public string ReportPath { get; } = Path.Combine(reportsDirectory, repo.Name + ".html");

    public bool HasReport => File.Exists(ReportPath);

    public string AnalyzedText => Repo.AnalyzedAt is { } at
        ? $"Analyzed {(Repo.AnalyzedRemotely ? "on GitHub " : "")}{Format.Relative(at)}"
        : "Not analyzed yet";

    public void Update(RegisteredRepo repo)
    {
        Repo = repo;
        OnPropertyChanged(string.Empty); // every displayed property may have changed
    }
}

internal static class Format
{
    /// <summary>
    /// A folder path short enough for the sidebar: with more than two folders, only the first and last are kept,
    /// so <c>C:\Users\me\code\Shop</c> becomes <c>C:/Users/.../Shop</c>.
    /// </summary>
    public static string ShortPath(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var folders = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var drive = root.TrimEnd('\\', '/').Replace('\\', '/');
        string[] shown = folders.Length > 2 ? [folders[0], "...", folders[^1]] : folders;
        return string.Join('/', shown.Prepend(drive));
    }

    public static string Relative(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.Now - at;
        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)elapsed.TotalMinutes} min ago",
            < 60 * 24 => $"{(int)elapsed.TotalHours} h ago",
            < 60 * 24 * 30 => $"{(int)elapsed.TotalDays} d ago",
            _ => at.LocalDateTime.ToString("MMM d, yyyy"),
        };
    }
}
