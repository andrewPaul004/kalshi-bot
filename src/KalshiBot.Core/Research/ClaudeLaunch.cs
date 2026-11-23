using System.Text.RegularExpressions;
using KalshiBot.Core.Common;

namespace KalshiBot.Core.Research;

/// <summary>
/// How to start the official Claude Code CLI. On Windows an npm install provides only a cmd.exe shim (claude.cmd).
/// Starting a batch file makes cmd.exe re-parse every argument, which can corrupt the JSON schema argument (and is the
/// "BatBadBut" argument-injection class). So the shim is read and unwrapped to the program it launches: node.exe plus
/// Claude Code's JS entry point, or a native .exe. Nothing is ever run through cmd.exe.
/// </summary>
public sealed record ClaudeLaunch(string FileName, IReadOnlyList<string> PrefixArguments)
{
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat"];

    public ProcessSpec Spec(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, string workDir, string? stdin, TimeSpan timeout) =>
        new(FileName, [.. PrefixArguments, .. args], env, workDir, stdin, timeout);

    public static ClaudeLaunch Resolve(string claudePath, IReadOnlyDictionary<string, string> env, bool windows,
        Func<string, bool> exists, Func<string, string> readFile)
    {
        if (!windows) return new ClaudeLaunch(claudePath, []);
        var found = FindOnWindows(claudePath, env, exists);
        if (!IsBatch(found)) return new ClaudeLaunch(found, []);

        var shim = readFile(found);
        var dir = Path.GetDirectoryName(found) ?? "";
        // npm cmd-shim: ... "%_prog%"  "%dp0%\node_modules\@anthropic-ai\claude-code\cli.js" %*   (older shims use %~dp0)
        var js = Regex.Match(shim, @"""%(?:dp0%|~dp0)\\(?<t>[^""%]+?\.js)""", RegexOptions.IgnoreCase);
        if (js.Success)
        {
            var node = Path.Combine(dir, "node.exe");
            if (!exists(node)) node = FindOnWindows("node", env, exists);
            if (IsBatch(node) || !node.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new KalshiBotException($"{found} needs node.exe, which was not found; pass --claude <path to claude.exe>.");
            return new ClaudeLaunch(node, [Path.Combine(dir, js.Groups["t"].Value)]);
        }
        var exe = Regex.Matches(shim, @"""%(?:dp0%|~dp0)\\(?<t>[^""%]+?\.exe)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["t"].Value)
            .FirstOrDefault(t => !Path.GetFileName(t.Replace('\\', '/')).Equals("node.exe", StringComparison.OrdinalIgnoreCase));
        if (exe is not null) return new ClaudeLaunch(Path.Combine(dir, exe), []);
        throw new KalshiBotException(
            $"{found} is a batch shim this runner cannot unwrap safely. Pass --claude <path to claude.exe> or install the native Claude Code build.");
    }

    /// <summary>Windows lookup: an explicit path with or without extension, else PATH order, .exe before .cmd/.bat per folder.</summary>
    internal static string FindOnWindows(string name, IReadOnlyDictionary<string, string> env, Func<string, bool> exists)
    {
        var ext = Path.GetExtension(name);
        if (WindowsExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return name;
        var explicitDir = name.Contains('\\') || name.Contains('/');
        var path = env.FirstOrDefault(kv => kv.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        var candidates = explicitDir
            ? WindowsExtensions.Select(e => name + e)
            : path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(d => WindowsExtensions.Select(e => Path.Combine(d.Trim('"'), name + e)));
        return candidates.FirstOrDefault(exists) ?? name;
    }

    private static bool IsBatch(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
