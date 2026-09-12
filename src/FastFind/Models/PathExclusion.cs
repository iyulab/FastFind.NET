namespace FastFind.Models;

/// <summary>
/// The single definition of what it means for a path to be excluded — by
/// <see cref="IndexingOptions.ExcludedPaths"/>, by
/// <see cref="Interfaces.MonitoringOptions.ExcludedPaths"/>, or by
/// <see cref="SearchQuery.ExcludedPaths"/>.
/// </summary>
/// <remarks>
/// <para>
/// An entry is read one of two ways:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A <b>fully qualified path</b> (<c>C:\proj\bin</c>, <c>/data/logs</c>) excludes what sits at or
/// under it, and nothing else — a longer segment that merely starts with it (<c>C:\proj\binaries</c>)
/// is not under it.
/// </description></item>
/// <item><description>
/// Anything else is a <b>run of whole segments</b> (<c>bin</c>, <c>src/bin</c>) and excludes any path
/// containing that run as complete segments. A name never matches inside a segment, so <c>temp</c>
/// leaves <c>C:\attempts</c> and <c>template.docx</c> alone.
/// </description></item>
/// </list>
/// <para>
/// There are no wildcards: <c>**/bin/**</c> is a literal name no path contains. On Windows <c>/</c>
/// and <c>\</c> name the same separator and comparison folds case; elsewhere only <c>/</c> separates
/// and comparison is case-sensitive, so a backslash is an ordinary file name character. Nothing here
/// rewrites a path — separators are folded in the comparison, never in the value.
/// </para>
/// </remarks>
public static class PathExclusion
{
    private static readonly bool FoldsSeparatorsAndCase = OperatingSystem.IsWindows();

    /// <summary>
    /// Whether <paramref name="path"/> is excluded by any of <paramref name="excludedPaths"/>.
    /// </summary>
    public static bool IsExcluded(string path, IList<string> excludedPaths) =>
        IsExcluded(path.AsSpan(), excludedPaths);

    /// <inheritdoc cref="IsExcluded(string, IList{string})"/>
    public static bool IsExcluded(ReadOnlySpan<char> path, IList<string> excludedPaths)
    {
        if (excludedPaths is null || excludedPaths.Count == 0 || path.IsEmpty) return false;

        for (var i = 0; i < excludedPaths.Count; i++)
        {
            if (Matches(path, excludedPaths[i])) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is excluded by the single entry
    /// <paramref name="excludedPath"/>.
    /// </summary>
    public static bool Matches(ReadOnlySpan<char> path, ReadOnlySpan<char> excludedPath)
    {
        if (path.IsEmpty || excludedPath.IsEmpty || excludedPath.IsWhiteSpace()) return false;

        if (Path.IsPathFullyQualified(excludedPath))
        {
            return IsAtOrUnder(path, excludedPath);
        }

        var run = TrimSeparators(excludedPath);
        return !run.IsEmpty && ContainsSegmentRun(path, run);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the path <paramref name="root"/> names, or sits beneath it.
    /// </summary>
    /// <remarks>
    /// An empty <paramref name="root"/> is the file system root itself, so every rooted path is
    /// under it. Both are compared as the host spells paths; neither is rewritten.
    /// </remarks>
    internal static bool IsAtOrUnder(ReadOnlySpan<char> path, ReadOnlySpan<char> scopeRoot)
    {
        var subject = TrimTrailingSeparators(path);
        var root = TrimTrailingSeparators(scopeRoot);

        if (root.IsEmpty) return subject.Length > 0 && IsSeparator(path[0]);
        if (subject.Length < root.Length) return false;
        if (!PathEquals(subject[..root.Length], root)) return false;

        return subject.Length == root.Length || IsSeparator(subject[root.Length]);
    }

    /// <summary>
    /// Whether two paths name the same location, ignoring a trailing separator.
    /// </summary>
    internal static bool SamePath(ReadOnlySpan<char> left, ReadOnlySpan<char> right) =>
        PathEquals(TrimTrailingSeparators(left), TrimTrailingSeparators(right));

    /// <summary>
    /// Whether <paramref name="path"/> contains <paramref name="run"/> as a run of whole segments.
    /// </summary>
    private static bool ContainsSegmentRun(ReadOnlySpan<char> path, ReadOnlySpan<char> run)
    {
        // A run always begins after a separator: an indexed path is rooted, so its first segment is
        // preceded by one, and matching at index 0 would let a drive or a leading segment match
        // half a name.
        for (var start = 1; start + run.Length <= path.Length; start++)
        {
            if (!IsSeparator(path[start - 1])) continue;
            if (!PathEquals(path.Slice(start, run.Length), run)) continue;

            var end = start + run.Length;
            if (end == path.Length || IsSeparator(path[end])) return true;
        }

        return false;
    }

    private static bool PathEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length) return false;
        if (!FoldsSeparatorsAndCase) return left.SequenceEqual(right);

        for (var i = 0; i < left.Length; i++)
        {
            if (Fold(left[i]) != Fold(right[i])) return false;
        }

        return true;
    }

    /// <summary>Windows only: one separator, one case.</summary>
    private static char Fold(char c) => c == '/' ? '\\' : char.ToUpperInvariant(c);

    private static bool IsSeparator(char c) => c == '/' || (FoldsSeparatorsAndCase && c == '\\');

    private static ReadOnlySpan<char> TrimTrailingSeparators(ReadOnlySpan<char> path)
    {
        var end = path.Length;
        while (end > 0 && IsSeparator(path[end - 1])) end--;
        return path[..end];
    }

    private static ReadOnlySpan<char> TrimSeparators(ReadOnlySpan<char> path)
    {
        var start = 0;
        while (start < path.Length && IsSeparator(path[start])) start++;
        return TrimTrailingSeparators(path[start..]);
    }
}
