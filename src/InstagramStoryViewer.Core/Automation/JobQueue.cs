namespace InstagramStoryViewer.Automation;

/// <summary>
/// Runs background jobs (auto-watch, auto-like, favorites) one at a time, in the order they were queued.
/// A job that is already queued or running is not queued again, so a refresh that fires while the previous
/// round is still busy cannot start a second, overlapping round. <see cref="CancelAll"/> stops the running
/// job through its token and drops the queued ones (sign-out, exit).
/// </summary>
/// <remarks>
/// Jobs continue on the context they were queued from (the UI thread in the app), because they update
/// UI-bound collections; that is why the awaits here keep the context.
/// </remarks>
public sealed class JobQueue : IDisposable
{
    private readonly Action<string, Exception> _onError;
    private readonly LinkedList<(string Name, Func<CancellationToken, Task> Run)> _pending = new();
    private CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private readonly object _gate = new();
    private string? _running;
    private bool _looping;
    private bool _disposed;

    /// <param name="onError">Called with the job name when a job throws (anything but cancellation).</param>
    public JobQueue(Action<string, Exception> onError) => _onError = onError;

    /// <summary>Name of the job that is running now, or null.</summary>
    public string? Running { get { lock (_gate) return _running; } }

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public bool IsQueuedOrRunning(string name)
    {
        lock (_gate) return IsQueuedOrRunningLocked(name);
    }

    private bool IsQueuedOrRunningLocked(string name) => _running == name || _pending.Any(p => p.Name == name);

    /// <summary>Queues a job. Returns false when a job with this name is already queued or running.</summary>
    public bool Enqueue(string name, Func<CancellationToken, Task> job)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsQueuedOrRunningLocked(name)) return false;
            _pending.AddLast((name, job));
            if (!_looping)
            {
                _looping = true;
                _loop = RunLoopAsync();
            }
            return true;
        }
    }

    /// <summary>Stops the running job and forgets the queued ones. Later jobs run normally.</summary>
    public void CancelAll()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            _pending.Clear();
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    /// <summary>Completes when nothing is running or queued (used by tests and on exit).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _loop;
    }

    private async Task RunLoopAsync()
    {
        // Yield first so Enqueue returns before the job starts (callers may queue several in a row).
        await Task.Yield();
        while (true)
        {
            (string Name, Func<CancellationToken, Task> Run) next;
            CancellationToken token;
            lock (_gate)
            {
                if (_pending.First is null)
                {
                    _looping = false;
                    return;
                }
                next = _pending.First.Value;
                _pending.RemoveFirst();
                _running = next.Name;
                token = _cts.Token;
            }
            try
            {
                await next.Run(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // cancelled by CancelAll
            }
            catch (Exception ex)
            {
                _onError(next.Name, ex);
            }
            finally
            {
                lock (_gate) _running = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CancelAll();
        _cts.Dispose();
    }
}
