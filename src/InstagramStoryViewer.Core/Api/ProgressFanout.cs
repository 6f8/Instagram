namespace InstagramStoryViewer.Api;

/// <summary>
/// Forwards progress to every subscriber, and gives a late subscriber the latest value at once, so a caller that
/// joins a download already under way sees where it is instead of 0% until the end.
/// Reports may arrive on any thread; each subscriber decides where to handle them (Progress&lt;T&gt; posts to the
/// thread that created it).
/// </summary>
internal sealed class ProgressFanout : IProgress<double>
{
    private readonly object _gate = new();
    private readonly List<IProgress<double>> _subscribers = new();
    private double? _last;

    public void Add(IProgress<double>? subscriber)
    {
        if (subscriber is null) return;
        double? last;
        lock (_gate)
        {
            _subscribers.Add(subscriber);
            last = _last;
        }
        if (last is { } value) subscriber.Report(value);
    }

    public void Report(double value)
    {
        IProgress<double>[] targets;
        lock (_gate)
        {
            _last = value;
            targets = _subscribers.ToArray();
        }
        foreach (var t in targets) t.Report(value);
    }
}
