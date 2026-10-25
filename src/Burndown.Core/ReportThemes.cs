namespace Burndown.Core;

/// <summary>The color themes the HTML report ships with. Each has a light and a dark variant.</summary>
public static class ReportThemes
{
    public const string Default = "atlas";

    /// <summary>In picker order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["atlas", "nord", "solarized", "contrast"];

    public static bool IsKnown(string name) => Names.Contains(name, StringComparer.OrdinalIgnoreCase);
}
