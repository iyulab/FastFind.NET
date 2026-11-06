namespace FastFind.Indexing;

/// <summary>
/// What an engine has measured about the searches it has answered: how many, how many matches, and how long
/// they took — the total, the fastest, the slowest.
/// </summary>
/// <remarks>
/// <para>
/// Each engine kept its own, and neither kept it right. The Windows engine added up
/// <c>Stopwatch.ElapsedMilliseconds</c>, which is an integer, so a search that took 0.4 ms — the usual case for one
/// that finds its answer early — added nothing, and it never recorded the number of matches or when the last search
/// was; the Unix engine reported a 10 ms average, a 1 ms fastest and a 100 ms slowest as constants, whatever it had
/// done. A statistic that reads as data and is a constant is worse than a missing one.
/// </para>
/// <para>Thread-safe. Times are kept in ticks and never rounded.</para>
/// </remarks>
internal sealed class SearchTimingTally
{
    private readonly object _gate = new();
    private long _searches;
    private long _matches;
    private long _totalTicks;
    private long _fastestTicks = long.MaxValue;
    private long _slowestTicks;
    private DateTime _lastSearchUtc;

    /// <summary>Records one answered search.</summary>
    public void Record(TimeSpan elapsed, long matches)
    {
        lock (_gate)
        {
            _searches++;
            _matches += matches;
            _totalTicks += elapsed.Ticks;
            if (elapsed.Ticks < _fastestTicks) _fastestTicks = elapsed.Ticks;
            if (elapsed.Ticks > _slowestTicks) _slowestTicks = elapsed.Ticks;
            _lastSearchUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Forgets everything measured so far.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _searches = 0;
            _matches = 0;
            _totalTicks = 0;
            _fastestTicks = long.MaxValue;
            _slowestTicks = 0;
            _lastSearchUtc = default;
        }
    }

    /// <summary>A consistent reading: every figure is from the same instant.</summary>
    public SearchTimingSnapshot Read()
    {
        lock (_gate)
        {
            return new SearchTimingSnapshot(
                _searches,
                _matches,
                TimeSpan.FromTicks(_totalTicks),
                _searches == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_fastestTicks),
                TimeSpan.FromTicks(_slowestTicks),
                _lastSearchUtc);
        }
    }
}

/// <summary>One reading of a <see cref="SearchTimingTally"/>. With no searches every time is zero.</summary>
internal readonly record struct SearchTimingSnapshot(
    long Searches,
    long Matches,
    TimeSpan Total,
    TimeSpan Fastest,
    TimeSpan Slowest,
    DateTime LastSearchUtc)
{
    /// <summary>The mean time of a search; zero when there have been none.</summary>
    public TimeSpan Average => Searches > 0 ? Total / Searches : TimeSpan.Zero;
}
