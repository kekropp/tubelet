using System.Threading.Channels;

namespace Tubelet.Pipeline;

/// <summary>
/// In-process wake-up between intake/scheduler (producers) and the DownloadCoordinator
/// (consumer). Coalescing: many signals collapse into one pending wake — the coordinator
/// drains the whole queue each time it wakes, so a dropped duplicate never loses work.
/// </summary>
public sealed class PipelineSignal
{
    private readonly Channel<byte> _ch =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Signal() => _ch.Writer.TryWrite(0);

    /// <summary>Wait for a signal or <paramref name="timeout"/>; returns regardless (poll fallback).</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _ch.Reader.ReadAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* timed out or shutting down */ }
    }
}
