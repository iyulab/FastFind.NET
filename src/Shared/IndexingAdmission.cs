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
}
