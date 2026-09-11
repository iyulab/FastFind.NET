using System.Text.RegularExpressions;

namespace FastFind.Models;

/// <summary>
/// The single definition of what it means for an item to satisfy a <see cref="SearchQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every index and persistence provider evaluates matches through this type, so that a query
/// returns the same set regardless of which backend answers it. Backends may narrow candidates
/// first — by SQL predicate, by an extension index, by a path trie — but narrowing is an
/// optimisation and this evaluator is the authority. A backend that reimplements the predicate
/// will drift from every other backend, which is the failure this type exists to prevent.
/// </para>
/// <para>
/// Path comparisons follow the host file system: case-insensitive on Windows, case-sensitive
/// elsewhere.
/// </para>
/// </remarks>
public static class SearchQueryEvaluator
{
    /// <summary>
    /// Comparison used for path and location matching on the current platform.
    /// </summary>
    public static StringComparison PathComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Builds the regular expression a query's text should be matched with, or <c>null</c> when
    /// the text is a plain substring match.
    /// </summary>
    /// <remarks>
    /// An explicit <see cref="SearchQuery.UseRegex"/> wins. Otherwise a pattern containing
    /// <c>*</c> or <c>?</c> is treated as a wildcard and anchored to the whole target. Build this
    /// once per query and pass it to <see cref="Matches"/> for every item; compiling per item is
    /// the difference between a fast search and a slow one.
    /// </remarks>
    public static Regex? CreateTextMatcher(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return query.GetCompiledRegex()
               ?? (HasWildcard(query.SearchText) ? query.GetWildcardRegex() : null);
    }

    /// <summary>
    /// Whether <paramref name="item"/> satisfies every constraint in <paramref name="query"/>.
    /// </summary>
    /// <param name="item">Item to test.</param>
    /// <param name="query">Query to test against.</param>
    /// <param name="textMatcher">
    /// Result of <see cref="CreateTextMatcher"/> for this query. Passing <c>null</c> means the
    /// query's text is matched as a substring.
    /// </param>
    public static bool Matches(in FastFileItem item, SearchQuery query, Regex? textMatcher)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Type filters
        if (!query.IncludeFiles && !item.IsDirectory) return false;
        if (!query.IncludeDirectories && item.IsDirectory) return false;
        if (!query.IncludeHidden && item.IsHidden) return false;
        if (!query.IncludeSystem && item.IsSystem) return false;

        // Size filters
        if (query.MinSize.HasValue && item.Size < query.MinSize.Value) return false;
        if (query.MaxSize.HasValue && item.Size > query.MaxSize.Value) return false;

        // Date filters. SearchQuery holds its bounds in UTC, matching the item's ticks.
        if (!IsWithin(item.CreatedTicks, query.MinCreatedDate, query.MaxCreatedDate)) return false;
        if (!IsWithin(item.ModifiedTicks, query.MinModifiedDate, query.MaxModifiedDate)) return false;

        // Attribute filters. RequiredAttributes must all be present; ExcludedAttributes rejects an
        // item carrying any of them — "exclude read-only or hidden" is one query, not two.
        if (query.RequiredAttributes.HasValue &&
            (item.Attributes & query.RequiredAttributes.Value) != query.RequiredAttributes.Value)
            return false;
        if (query.ExcludedAttributes.HasValue &&
            (item.Attributes & query.ExcludedAttributes.Value) != 0)
            return false;

        // Extension filter, normalized so that "cs" and ".cs" mean the same thing
        if (!string.IsNullOrEmpty(query.ExtensionFilter) && !MatchesExtension(item.Extension, query.ExtensionFilter))
            return false;

        // Location scope
        if (!MatchesLocation(item, query)) return false;

        return MatchesText(item, query, textMatcher);
    }

    /// <summary>
    /// Whether a query constrains size or timestamps — the metadata an index built without
    /// <see cref="IndexingOptions.CollectFileMetadata"/> may not have.
    /// </summary>
    /// <remarks>
    /// An engine uses this to refuse such a query rather than answer it from values that were
    /// never read, which would return an empty result indistinguishable from "no such files".
    /// </remarks>
    public static bool RequiresFileMetadata(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return query.MinSize.HasValue || query.MaxSize.HasValue
            || query.MinCreatedDate.HasValue || query.MaxCreatedDate.HasValue
            || query.MinModifiedDate.HasValue || query.MaxModifiedDate.HasValue;
    }

    /// <summary>
    /// Whether a timestamp falls inside a date range. A timestamp that was not collected — zero
    /// ticks, <see cref="DateTime.MinValue"/> — satisfies no bound: an unknown time is neither
    /// before nor after anything.
    /// </summary>
    private static bool IsWithin(long ticks, DateTime? min, DateTime? max)
    {
        if (min is null && max is null) return true;
        if (ticks == 0) return false;
        if (min is { } lower && ticks < lower.Ticks) return false;
        if (max is { } upper && ticks > upper.Ticks) return false;
        return true;
    }

    /// <summary>
    /// Whether an item's extension satisfies a filter, treating a leading dot as optional on both
    /// sides.
    /// </summary>
    public static bool MatchesExtension(string extension, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;

        var itemExtension = extension.AsSpan().TrimStart('.');
        var filterExtension = filter.AsSpan().TrimStart('.');

        return itemExtension.Equals(filterExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an item falls inside the query's <see cref="SearchQuery.BasePath"/> or
    /// <see cref="SearchQuery.SearchLocations"/>, and outside its
    /// <see cref="SearchQuery.ExcludedPaths"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="SearchQuery.BasePath"/> takes precedence over
    /// <see cref="SearchQuery.SearchLocations"/>, matching how candidates are selected from an
    /// in-memory index. A query with neither is unscoped.
    /// </remarks>
    public static bool MatchesLocation(in FastFileItem item, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var directory = item.DirectoryPath;

        if (!string.IsNullOrEmpty(query.BasePath))
        {
            if (!IsUnder(directory, query.BasePath, query.IncludeSubdirectories)) return false;
        }
        else if (query.SearchLocations.Count > 0)
        {
            var anyMatch = false;
            foreach (var location in query.SearchLocations)
            {
                if (IsUnder(directory, location, query.IncludeSubdirectories))
                {
                    anyMatch = true;
                    break;
                }
            }

            if (!anyMatch) return false;
        }

        foreach (var excluded in query.ExcludedPaths)
        {
            if (IsUnder(directory, excluded, includeSubdirectories: true)) return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a directory sits at, or beneath, a scope root.
    /// </summary>
    /// <remarks>
    /// On Windows <c>/</c> is a separator too, so <c>C:/data</c> and <c>C:\data</c> name the same
    /// scope. The paths are compared with separators folded; neither is rewritten. Elsewhere a
    /// backslash is an ordinary file name character and nothing is folded.
    /// </remarks>
    public static bool IsUnder(string directoryPath, string scopeRoot, bool includeSubdirectories)
    {
        if (string.IsNullOrEmpty(scopeRoot)) return true;
        if (string.IsNullOrEmpty(directoryPath)) return false;

        var directory = TrimTrailingSeparator(FoldSeparators(directoryPath));
        var root = TrimTrailingSeparator(FoldSeparators(scopeRoot));

        if (directory.Equals(root, PathComparison)) return true;
        if (!includeSubdirectories) return false;

        return directory.Length > root.Length
               && directory.StartsWith(root, PathComparison)
               && IsSeparator(directory[root.Length]);
    }

    private static bool MatchesText(in FastFileItem item, SearchQuery query, Regex? textMatcher)
    {
        var searchText = query.SearchText;
        if (string.IsNullOrEmpty(searchText)) return true;

        var target = query.SearchFileNameOnly ? item.Name : item.FullPath;

        if (textMatcher is not null) return textMatcher.IsMatch(target);

        if (query.CaseSensitive) return target.Contains(searchText, StringComparison.Ordinal);

        // SIMD-accelerated case-insensitive substring match.
        return SIMDStringMatcher.ContainsVectorized(target.AsSpan(), searchText.AsSpan());
    }

    private static bool HasWildcard(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains('*') || text.Contains('?'));

    private static string TrimTrailingSeparator(string path) => path.TrimEnd('\\', '/');

    // string.Replace returns the same instance when there is nothing to replace, so the common case
    // — a Windows path that already uses backslashes — allocates nothing.
    private static string FoldSeparators(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
