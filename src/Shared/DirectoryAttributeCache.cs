namespace FastFind.Indexing;

/// <summary>
/// What an engine remembers of the attributes of the directories above the changes its monitor reports, so that a
/// burst of changes in one place reads each of those directories once rather than once per change.
/// </summary>
/// <remarks>
/// <para>
/// A monitored change is checked against every directory between the monitored location and the item
/// (<see cref="IndexingAdmission.IsBeneathSkippedDirectoryAsync"/>). Consecutive changes of a burst share almost all
/// of those directories; read afresh, a change nine directories deep cost about four times the change's own file-info
/// call (<c>MonitoredChangeCostTests</c>).
/// </para>
/// <para>
/// An entry is a directory's <em>own</em> attributes, never a verdict: what is hidden is asked of the options at the
/// moment of the check, so a cache outlives a change of options, and what a directory is does not depend on its
/// ancestors. That is also why invalidation is one path and not a subtree — a directory's attributes change, its
/// descendants' do not. An entry is dropped by the monitor's own report of a change to that path
/// (<see cref="Invalidate"/>), expires after <see cref="DefaultLifetime"/> whatever was reported (a monitor that does not
/// deliver attribute changes is wrong for at most that long), and is never made of a read that failed. When the cache
/// is full it is emptied, which is simpler than a policy and costs one re-read of the active directories.
/// </para>
/// </remarks>
internal sealed class DirectoryAttributeCache
{
    /// <summary>How long an entry is believed without a report of a change to it.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);

    /// <summary>How many directories are remembered before the cache is emptied.</summary>
    public const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<string, (FileAttributes Attributes, long ExpiresUtcTicks)> _entries =
        new(FileEntryTable.PathKeyComparer.Instance);
    private readonly TimeProvider _time;
    private readonly long _lifetimeTicks;
    private readonly int _capacity;

    public DirectoryAttributeCache(TimeProvider? timeProvider = null, TimeSpan? lifetime = null, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = timeProvider ?? TimeProvider.System;
        _lifetimeTicks = (lifetime ?? DefaultLifetime).Ticks;
        _capacity = capacity;
    }

    /// <summary>How many directories are remembered now (expired ones are counted until they are asked for).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>The remembered attributes of <paramref name="directory"/>, if they are remembered and not expired.</summary>
    public bool TryGet(string directory, out FileAttributes attributes)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(directory, out var entry))
            {
                if (entry.ExpiresUtcTicks > _time.GetUtcNow().UtcTicks)
                {
                    attributes = entry.Attributes;
                    return true;
                }

                _entries.Remove(directory);
            }
        }

        attributes = default;
        return false;
    }

    /// <summary>Remembers the attributes just read from <paramref name="directory"/>.</summary>
    public void Set(string directory, FileAttributes attributes)
    {
        lock (_gate)
        {
            if (_entries.Count >= _capacity && !_entries.ContainsKey(directory))
                _entries.Clear();

            _entries[directory] = (attributes, _time.GetUtcNow().UtcTicks + _lifetimeTicks);
        }
    }

    /// <summary>Forgets <paramref name="path"/>: the monitor reported a change to it. Nothing beneath it is forgotten.</summary>
    public void Invalidate(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_gate) _entries.Remove(path);
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}
