namespace PotatoDM.Core.Transfer;

/// <summary>Sliding-window throughput meter used for the progress event.</summary>
public sealed class SpeedMeter
{
    private readonly Queue<(DateTimeOffset Time, long Total)> _samples = new();
    private readonly TimeSpan _window;

    public SpeedMeter(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(2);
    }

    public void Add(long totalBytes, DateTimeOffset now)
    {
        _samples.Enqueue((now, totalBytes));

        while (_samples.Count > 1 && now - _samples.Peek().Time > _window)
        {
            _samples.Dequeue();
        }
    }

    public double BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return 0;
            }

            var first = _samples.Peek();
            var last = _samples.Last();
            var seconds = (last.Time - first.Time).TotalSeconds;
            if (seconds <= 0.05)
            {
                return 0;
            }

            return Math.Max(0, (last.Total - first.Total) / seconds);
        }
    }
}
