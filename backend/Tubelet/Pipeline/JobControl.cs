using System.Collections.Concurrent;

namespace Tubelet.Pipeline;

/// <summary>
/// Tracks the cancellation source of each in-flight job so the /cancel endpoint can kill a
/// running yt-dlp/ffmpeg process tree (Proc kills on cancel). Rebuilt from nothing on restart —
/// the durable job state lives in SQLite, not here.
/// </summary>
public sealed class JobControl
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    /// <summary>Register a job as running; returns a token linked to <paramref name="parent"/>.</summary>
    public CancellationTokenSource Register(long jobId, CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _running[jobId] = cts;
        return cts;
    }

    public void Unregister(long jobId)
    {
        if (_running.TryRemove(jobId, out var cts)) cts.Dispose();
    }

    /// <summary>Cancel a running job's subprocess. Returns true if it was running.</summary>
    public bool Cancel(long jobId)
    {
        if (!_running.TryGetValue(jobId, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }
}
