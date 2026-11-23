namespace KalshiBot.Cli;

/// <summary>Minimal argument parser: positionals plus --name value / --flag options. Unknown options are errors.</summary>
public sealed class Args
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    public List<string> Positionals { get; } = [];

    public Args(IEnumerable<string> argv, IReadOnlySet<string> flags)
    {
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                if (flags.Contains(name)) { Add(name, "true"); continue; }
                if (i + 1 >= list.Count) throw new UsageException($"Option --{name} needs a value.");
                Add(name, list[++i]);
            }
            else Positionals.Add(a);
        }
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var v)) _options[name] = v = [];
        v.Add(value);
    }

    public string? Opt(string name) { _used.Add(name); return _options.TryGetValue(name, out var v) ? v[^1] : null; }
    public List<string> Many(string name) { _used.Add(name); return _options.TryGetValue(name, out var v) ? v : []; }
    public bool Flag(string name) => Opt(name) == "true";

    public string Pos(int i, string what) =>
        i < Positionals.Count ? Positionals[i] : throw new UsageException($"Missing {what}.");

    public void EnsureAllUsed()
    {
        var unknown = _options.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new UsageException("Unknown option(s): " + string.Join(", ", unknown.Select(u => "--" + u)));
    }
}

public sealed class UsageException(string message) : Exception(message);
