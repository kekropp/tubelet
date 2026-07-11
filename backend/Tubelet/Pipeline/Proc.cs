using System.Diagnostics;
using System.Text;

namespace Tubelet.Pipeline;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Thin wrapper over Process for short-lived subprocesses (yt-dlp, ffmpeg, ffprobe).
/// Captures stdout/stderr fully (RunAsync) or streams stdout line-by-line (StreamAsync).
/// Cancellation kills the whole process tree so a cancelled download leaves nothing running.
/// </summary>
public static class Proc
{
    /// <summary>Render a command line for logs (quotes args with spaces; copy-paste runnable).</summary>
    public static string Render(string file, IEnumerable<string> args) =>
        file + " " + string.Join(' ', args.Select(a =>
            a.Length == 0 || a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));

    /// <summary>Last <paramref name="lines"/> non-empty lines of process output, for log tails.</summary>
    public static string Tail(string? text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(empty)";
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    private static ProcessStartInfo Info(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Run to completion, buffering all output. For metadata/probe/version calls.</summary>
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = Info(file, args) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            throw;
        }
        return new ProcResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Run while invoking <paramref name="onStdoutLine"/> for each stdout line as it arrives
    /// (progress parsing). stderr is ring-buffered and returned with the exit code.
    /// </summary>
    public static async Task<ProcResult> StreamAsync(
        string file, IEnumerable<string> args, Action<string> onStdoutLine,
        int stderrRingLines = 50, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = Info(file, args) };
        var errRing = new Queue<string>();

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errRing)
            {
                errRing.Enqueue(e.Data);
                while (errRing.Count > stderrRingLines) errRing.Dequeue();
            }
        };

        p.Start();
        p.BeginErrorReadLine();

        try
        {
            // Read stdout ourselves so we can hand each line to the caller immediately.
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                onStdoutLine(line);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            throw;
        }

        string stderr;
        lock (errRing) stderr = string.Join('\n', errRing);
        return new ProcResult(p.ExitCode, "", stderr);
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }
}
