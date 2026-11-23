using System.Diagnostics;

namespace KalshiBot.Core.Research;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory, string? Stdin, TimeSpan Timeout);

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>Runs a process with exactly the given environment (inherited variables are cleared first).</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {spec.FileName}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (spec.Stdin is not null) await p.StandardInput.WriteAsync(spec.Stdin.AsMemory(), ct);
        p.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(spec.Timeout);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            p.Kill(entireProcessTree: true);
            return new ProcessResult(-1, await stdout, await stderr, TimedOut: true);
        }
        return new ProcessResult(p.ExitCode, await stdout, await stderr, TimedOut: false);
    }
}
