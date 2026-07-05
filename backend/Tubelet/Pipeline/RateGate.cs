namespace Tubelet.Pipeline;

/// <summary>
/// Global token bucket in front of every yt-dlp spawn (metadata calls included), so Tubelet
/// stays a polite bot. Capacity/refill come from Settings → Network (ops/hour) and reload live.
/// User pastes bypass the bucket (priority 1) but never the per-invocation sleep flags.
///
/// The clock is injectable so the refill logic can be unit-tested without real time.
/// </summary>
public sealed class RateGate(int opsPerHour, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private double _tokens;
    private double _capacity = Math.Max(1, opsPerHour);
    private double _refillPerSecond = Math.Max(1, opsPerHour) / 3600.0;
    private DateTimeOffset _last;
    private bool _started;

    /// <summary>Live-reload the bucket rate from settings; keeps current tokens (clamped to new capacity).</summary>
    public void Reconfigure(int opsPerHour)
    {
        lock (_lock)
        {
            Refill();
            _capacity = Math.Max(1, opsPerHour);
            _refillPerSecond = _capacity / 3600.0;
            _tokens = Math.Min(_tokens, _capacity);
        }
    }

    /// <summary>Take a token without waiting. Returns false if the bucket is empty right now.</summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }

    /// <summary>Seconds until the next token is available (0 if one is available now).</summary>
    public double SecondsUntilNext()
    {
        lock (_lock)
        {
            Refill();
            return _tokens >= 1 ? 0 : (1 - _tokens) / _refillPerSecond;
        }
    }

    /// <summary>Block until a token is available (or cancellation). Polls in bounded sleeps.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        while (!TryAcquire())
        {
            var wait = SecondsUntilNext();
            var delay = TimeSpan.FromSeconds(Math.Clamp(wait, 0.05, 30));
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = _now();
        if (!_started)
        {
            _started = true;
            _last = now;
            _tokens = _capacity; // start full so the first pastes/scan go through immediately
            return;
        }
        var elapsed = (now - _last).TotalSeconds;
        if (elapsed <= 0) return;
        _last = now;
        _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerSecond);
    }
}
