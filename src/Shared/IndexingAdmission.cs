using FastFind.Interfaces;
using FastFind.Models;

namespace FastFind.Indexing;

/// <summary>
/// The one definition of what an index admits: whether an item the file system produced belongs in an
/// index built with a given <see cref="IndexingOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// It is the indexing-side counterpart of <see cref="SearchQueryEvaluator"/>, which defines what
/// satisfies a query. The predicate used to be written out in each place that applied it — the
/// directory-walk provider, the change-journal provider, and (not at all) the live scan that stands in
/// for an index that is still empty — and the places did not agree: that scan returned what indexing
/// had excluded, so an exclusion list appeared to do nothing for as long as indexing took.
/// </para>
/// <para>
/// This is the whole of the options' per-item rules. Which <em>directories</em> a walk does not enter
/// is a separate, cheaper question that follows from the same options: a path beneath an excluded
/// directory is itself excluded, so a walker may skip the subtree without asking about each entry.
/// </para>
/// </remarks>
internal static class IndexingAdmission
{
    /// <summary>
    /// The attributes that make a directory one an indexing pass does not enter: with hidden files off a hidden
    /// directory is skipped <em>with everything beneath it</em> (<c>.git</c>, <c>AppData</c>, a macOS
    /// <c>~/Library</c>), and likewise a system directory with system files off. Every provider applies this
    /// to a directory, not only to each entry's own attributes, so the same folder indexes to the same entries
    /// whichever pass reads it.
    /// </summary>
    public static FileAttributes AttributesToSkip(IndexingOptions options)
    {
        // Starts from nothing. It started from FileAttributes.Normal, which is not "no attributes": it is
        // the one attribute a file carries when it has no other (its archive bit cleared by a backup tool,
        // a restore, some file systems), so every such file was skipped.
        var skip = default(FileAttributes);

        if (!options.IncludeHidden)
            skip |= FileAttributes.Hidden;

        if (!options.IncludeSystem)
            skip |= FileAttributes.System;

        return skip;
    }

    /// <summary>
    /// Whether <paramref name="file"/> belongs in an index built with <paramref name="options"/>.
    /// </summary>
    public static bool Admits(FileItem file, IndexingOptions options)
    {
        // Check hidden files
        if (!options.IncludeHidden && file.IsHidden)
            return false;

        // Check system files
        if (!options.IncludeSystem && file.IsSystem)
            return false;

        // Check file size
        if (options.MaxFileSize.HasValue && file.Size > options.MaxFileSize.Value)
            return false;

        // Check excluded paths
        if (PathExclusion.IsExcluded(file.FullPath, options.ExcludedPaths))
            return false;

        // Check excluded extensions
        if (!string.IsNullOrEmpty(file.Extension) &&
            options.ExcludedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Puts an item a monitor reported into <paramref name="index"/> when an indexing pass with
    /// <paramref name="options"/> would have admitted it.
    /// </summary>
    /// <remarks>
    /// A change the monitor reports used to skip all of the above and the file-count cap, so files the
    /// options refuse were added, and an index capped to bound memory grew with every file created after
    /// the pass. The cap is a ceiling the index keeps: an item it already holds may still change, a new
    /// one is added only below it, and a deletion makes room. An entry that is no longer admissible (it
    /// became hidden, was renamed to an excluded extension) is removed: the pass would not have indexed it.
    /// </remarks>
    public static async Task<MonitoredOutcome> IndexMonitoredAsync(
        ISearchIndex index, FileItem item, IndexingOptions options, CancellationToken cancellationToken = default)
    {
        if (!Admits(item, options))
        {
            await index.RemoveAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
            return MonitoredOutcome.NotAdmitted;
        }

        if (options.MaxFileCount is { } cap
            && index.Count >= cap
            && !await index.ContainsAsync(item.FullPath, cancellationToken).ConfigureAwait(false))
        {
            return MonitoredOutcome.CapReached;
        }

        await index.UpdateAsync(item.ToFastFileItem(), cancellationToken).ConfigureAwait(false);
        return MonitoredOutcome.Indexed;
    }
}

/// <summary>What became of an item a monitor reported.</summary>
internal enum MonitoredOutcome
{
    /// <summary>It is in the index (added, or an existing entry updated).</summary>
    Indexed,

    /// <summary>The indexing options refuse it; no entry for it remains.</summary>
    NotAdmitted,

    /// <summary>The index holds <see cref="IndexingOptions.MaxFileCount"/> items and this one is new.</summary>
    CapReached,
}
