using System.Runtime.InteropServices;

namespace CodeAtlas.Cli;

/// <summary>
/// Color for terminal output. It's off when output is redirected (CI logs, files), when
/// <c>NO_COLOR</c> is set (https://no-color.org), and when the console can't interpret escape codes,
/// so the text stays clean everywhere it isn't shown in a terminal.
/// </summary>
internal static class Ansi
{
    private static readonly bool Enabled = Detect();

    public static string Green(string text) => Wrap("32", text);

    public static string Red(string text) => Wrap("31", text);

    public static string Yellow(string text) => Wrap("33", text);

    public static string Bold(string text) => Wrap("1", text);

    public static string Dim(string text) => Wrap("2", text);

    private static string Wrap(string code, string text) => Enabled ? $"\e[{code}m{text}\e[0m" : text;

    private static bool Detect()
    {
        if (Console.IsOutputRedirected
            || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("TERM") == "dumb")
        {
            return false;
        }

        return !OperatingSystem.IsWindows() || TryEnableVirtualTerminal();
    }

    /// <summary>Older Windows consoles only interpret escape codes once virtual terminal processing is switched on.</summary>
    private static bool TryEnableVirtualTerminal()
    {
        const int StandardOutput = -11;
        const uint EnableVirtualTerminalProcessing = 0x0004;

        var handle = GetStdHandle(StandardOutput);
        return GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    // Classic DllImport rather than LibraryImport: the source-generated version requires compiling the whole
    // CLI with unsafe code enabled, which isn't worth it for three console calls.
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint handle, uint mode);
#pragma warning restore SYSLIB1054
}
