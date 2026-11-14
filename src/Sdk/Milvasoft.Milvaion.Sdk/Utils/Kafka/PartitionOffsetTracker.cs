namespace Milvasoft.Milvaion.Sdk.Utils.Kafka;

/// <summary>
/// Tracks the records of one partition that were handed out but are not finished yet, so that the offset committed
/// is never past a record that has not been finished. Records may finish in any order; the committable offset is the
/// lowest unfinished offset, or one past the highest seen when everything is finished.
/// </summary>
/// <remarks>Thread-safe. Offsets must be added in increasing order, which is the order a consumer reads them in.</remarks>
public sealed class PartitionOffsetTracker
{
    private readonly SortedSet<long> _pending = [];
    private readonly Lock _lock = new();
    private long _highestSeen = -1;
    private long _lastCommitted = -1;

    /// <summary>
    /// Records handed out and not finished yet.
    /// </summary>
    public int InFlight
    {
        get
        {
            lock (_lock)
                return _pending.Count;
        }
    }

    /// <summary>
    /// Registers a record that is about to be processed.
    /// </summary>
    public void Add(long offset)
    {
        lock (_lock)
        {
            _pending.Add(offset);

            if (offset > _highestSeen)
                _highestSeen = offset;
        }
    }

    /// <summary>
    /// Marks a record finished. Returns <see langword="false"/> when it was not pending (already finished, or never added).
    /// </summary>
    public bool Complete(long offset)
    {
        lock (_lock)
            return _pending.Remove(offset);
    }

    /// <summary>
    /// The offset to commit (the next offset to read), or <see langword="null"/> when nothing new can be committed.
    /// </summary>
    public long? GetCommittable()
    {
        lock (_lock)
        {
            if (_highestSeen < 0)
                return null;

            var next = _pending.Count > 0 ? _pending.Min : _highestSeen + 1;

            return next > _lastCommitted ? next : null;
        }
    }

    /// <summary>
    /// Remembers that <paramref name="offset"/> was committed.
    /// </summary>
    public void MarkCommitted(long offset)
    {
        lock (_lock)
        {
            if (offset > _lastCommitted)
                _lastCommitted = offset;
        }
    }
}
