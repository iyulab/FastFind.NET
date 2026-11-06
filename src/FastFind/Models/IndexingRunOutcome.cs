namespace FastFind.Models;

/// <summary>
/// How the most recent indexing pass ended, as reported by
/// <see cref="IndexingStatistics.LastRunOutcome"/>.
/// </summary>
/// <remarks>
/// Before this, a pass that lost part of what it was asked to read looked exactly like one that read
/// all of it: the index was smaller than it should be and nothing said so. Anything other than
/// <see cref="Completed"/> means the index may be missing entries the caller expects.
/// </remarks>
public enum IndexingRunOutcome
{
    /// <summary>No indexing pass has run.</summary>
    None = 0,

    /// <summary>The pass read every location it was given.</summary>
    Completed,

    /// <summary>
    /// The pass ran to the end, but one or more locations could not be read: see
    /// <see cref="IndexingStatistics.FailedLocations"/>. The index lacks what was in them.
    /// </summary>
    CompletedWithGaps,

    /// <summary>The pass stopped at <see cref="IndexingOptions.MaxFileCount"/>; files beyond the cap are not indexed.</summary>
    CapReached,

    /// <summary>The pass was cut short by the engine's indexing time limit; what was read so far is searchable.</summary>
    TimedOut,

    /// <summary>The pass was stopped by the caller; what was read so far is searchable.</summary>
    Cancelled,

    /// <summary>The pass ended on an error.</summary>
    Failed,
}
