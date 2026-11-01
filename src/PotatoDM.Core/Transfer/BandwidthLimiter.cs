namespace PotatoDM.Core.Transfer;

/// <summary>Paces downloads to a byte-per-second budget.</summary>
public interface IBandwidthLimiter
{
    /// <summary>
    /// Waits until the next <paramref name="bytes"/> may be transferred.
    /// Implementations must be safe for concurrent callers.
    /// </summary>
    ValueTask ThrottleAsync(int bytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// A shared, reservation-based pacer. Each call reserves a time slot for its
/// bytes and returns once that slot begins, so concurrent workers queue fairly
/// instead of each sleeping independently. A rate of zero disables limiting.
/// </summary>
public sealed class BandwidthLimiter : IBandwidthLimiter
{
    private readonly Func<long> _rateProvider;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private long _nextAvailableTimestamp;
    private long _lastRate;
    private bool _hasReservation;

    /// <summary>Creates a limiter with a fixed rate; zero means unlimited.</summary>
    public BandwidthLimiter(long maxBytesPerSecond, TimeProvider? timeProvider = null)
        : this(() => maxBytesPerSecond, timeProvider)
    {
        if (maxBytesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytesPerSecond),
                maxBytesPerSecond,
                "The bandwidth cap cannot be negative.");
        }
    }

    /// <summary>
    /// Creates a limiter whose rate is read on every call, so settings changes
    /// take effect without rebuilding in-flight requests.
    /// </summary>
    public BandwidthLimiter(Func<long> rateProvider, TimeProvider? timeProvider = null)
    {
        _rateProvider = rateProvider ?? throw new ArgumentNullException(nameof(rateProvider));
        _time = timeProvider ?? TimeProvider.System;
    }

    public long MaxBytesPerSecond => Math.Max(0, _rateProvider());

    public async ValueTask ThrottleAsync(
        int bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes <= 0)
        {
            return;
        }

        TimeSpan delay;

        lock (_gate)
        {
            var rate = Math.Max(0, _rateProvider());
            if (rate == 0)
            {
                _hasReservation = false;
                _lastRate = 0;
                return;
            }

            var now = _time.GetTimestamp();

            // Re-scale an outstanding reservation when the setting changed, so
            // raising the cap actually speeds up queued workers.
            if (_hasReservation && _lastRate > 0 && _lastRate != rate)
            {
                var remaining = Math.Max(0, _nextAvailableTimestamp - now);
                _nextAvailableTimestamp =
                    now + (long)Math.Ceiling(remaining * (double)_lastRate / rate);
            }

            _lastRate = rate;

            var start = _hasReservation && _nextAvailableTimestamp > now
                ? _nextAvailableTimestamp
                : now;
            var duration = (long)Math.Ceiling(
                bytes * ((double)_time.TimestampFrequency / rate));

            _nextAvailableTimestamp = start + Math.Max(1, duration);
            _hasReservation = true;
            delay = _time.GetElapsedTime(now, start);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
