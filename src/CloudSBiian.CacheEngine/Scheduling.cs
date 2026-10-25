using CloudSBiian.Core.Abstractions;

namespace CloudSBiian.CacheEngine;

/// <summary>
/// Per-account concurrency limiter with priorities. One slot is always reserved for P0/P1 so
/// prefetch can never starve active reads, and a waiting P0 request preempts (cancels) the
/// lowest-priority running prefetch.
/// </summary>
public sealed class PriorityLimiter
{
    private readonly object _gate = new();
    private readonly List<Lease> _running = new();
    private readonly List<Waiter> _waiters = new();
    private long _sequence;

    public PriorityLimiter(int maxConcurrency) => MaxConcurrency = Math.Max(1, maxConcurrency);

    public int MaxConcurrency { get; set; }

    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>Raised when a running prefetch was cancelled to make room for an active read.</summary>
    public event EventHandler? Preempted;

    public async Task<Lease> AcquireAsync(FetchPriority priority, CancellationToken ct)
    {
        Waiter waiter;
        Lease? victim = null;
        lock (_gate)
        {
            if (CanRun(priority))
            {
                var lease = new Lease(this, priority);
                _running.Add(lease);
                return lease;
            }

            waiter = new Waiter(priority, Interlocked.Increment(ref _sequence));
            _waiters.Add(waiter);
            if (priority == FetchPriority.P0)
            {
                victim = _running
                    .Where(l => l.Priority >= FetchPriority.P1 && !l.IsPreempted)
                    .OrderByDescending(l => l.Priority)
                    .FirstOrDefault();
            }
        }

        if (victim != null)
        {
            victim.Preempt();
            Preempted?.Invoke(this, EventArgs.Empty);
        }

        using (ct.Register(() =>
               {
                   lock (_gate)
                   {
                       if (_waiters.Remove(waiter))
                       {
                           waiter.Completion.TrySetCanceled(ct);
                       }
                   }
               }))
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
    }

    private bool CanRun(FetchPriority priority)
    {
        var reserved = MaxConcurrency > 1 ? 1 : 0;
        var limit = priority <= FetchPriority.P1 ? MaxConcurrency : MaxConcurrency - reserved;
        // Speculative work also waits while an active read is queued.
        if (priority >= FetchPriority.P2 && _waiters.Any(w => w.Priority <= FetchPriority.P1))
        {
            return false;
        }

        return _running.Count < Math.Max(1, limit);
    }

    internal void Release(Lease lease)
    {
        lock (_gate)
        {
            _running.Remove(lease);
            while (_waiters.Count > 0)
            {
                var next = _waiters.OrderBy(w => w.Priority).ThenBy(w => w.Sequence).First();
                if (!CanRunIgnoringQueue(next.Priority))
                {
                    break;
                }

                _waiters.Remove(next);
                var granted = new Lease(this, next.Priority);
                _running.Add(granted);
                if (!next.Completion.TrySetResult(granted))
                {
                    _running.Remove(granted);
                }
            }
        }
    }

    private bool CanRunIgnoringQueue(FetchPriority priority)
    {
        var reserved = MaxConcurrency > 1 ? 1 : 0;
        var limit = priority <= FetchPriority.P1 ? MaxConcurrency : MaxConcurrency - reserved;
        return _running.Count < Math.Max(1, limit);
    }

    private sealed class Waiter(FetchPriority priority, long sequence)
    {
        public FetchPriority Priority { get; } = priority;

        public long Sequence { get; } = sequence;

        public TaskCompletionSource<Lease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Lease : IDisposable
    {
        private readonly PriorityLimiter _owner;
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;

        internal Lease(PriorityLimiter owner, FetchPriority priority)
        {
            _owner = owner;
            Priority = priority;
        }

        public FetchPriority Priority { get; }

        /// <summary>Cancelled when the lease is preempted by an active read.</summary>
        public CancellationToken Token => _cts.Token;

        public bool IsPreempted { get; private set; }

        internal void Preempt()
        {
            IsPreempted = true;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(this);
                _cts.Dispose();
            }
        }
    }
}

/// <summary>Token bucket used to cap prefetch bandwidth. A limit of 0 means unlimited.</summary>
public sealed class BandwidthLimiter
{
    private readonly object _gate = new();
    private readonly Func<long> _bytesPerSecond;
    private double _tokens;
    private long _lastTicks = Environment.TickCount64;

    public BandwidthLimiter(Func<long> bytesPerSecond) => _bytesPerSecond = bytesPerSecond;

    public async Task ConsumeAsync(long bytes, CancellationToken ct)
    {
        while (true)
        {
            var rate = _bytesPerSecond();
            if (rate <= 0)
            {
                return;
            }

            TimeSpan wait;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                _tokens = Math.Min(rate, _tokens + ((now - _lastTicks) / 1000.0 * rate));
                _lastTicks = now;
                if (_tokens >= bytes || _tokens >= rate)
                {
                    _tokens -= bytes;
                    return;
                }

                wait = TimeSpan.FromMilliseconds(Math.Clamp((bytes - _tokens) / rate * 1000.0, 5, 1000));
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}
