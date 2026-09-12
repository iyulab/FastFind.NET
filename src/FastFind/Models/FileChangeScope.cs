namespace FastFind.Models;

/// <summary>
/// The single definition of what a file system change looks like from inside a boundary — the set
/// of monitored locations, or the paths an exclusion list leaves out.
/// </summary>
/// <remarks>
/// <para>
/// A change that does not touch the boundary is either reported as it is or not at all. A
/// <b>rename is the case that matters</b>: one that leaves the boundary is, from inside it, the
/// deletion of the path that was there; one that arrives is the creation of the path that now is.
/// Without that fold a file renamed into an excluded directory is simply dropped, and the index
/// keeps a path the file no longer has.
/// </para>
/// <para>
/// The predicate is supplied by the caller so that the same fold serves both boundaries, and
/// composing two calls restricts a change to both at once — first the monitored locations, then
/// what the exclusion list leaves out.
/// </para>
/// </remarks>
public static class FileChangeScope
{
    /// <summary>
    /// Restricts <paramref name="change"/> to the boundary <paramref name="isInside"/> describes,
    /// returning the change as it reads from inside, or <see langword="null"/> when it is not
    /// visible there at all.
    /// </summary>
    /// <param name="change">The change to restrict; <see langword="null"/> passes through.</param>
    /// <param name="isInside">Whether a path lies inside the boundary.</param>
    public static FileChangeEventArgs? Restrict(FileChangeEventArgs? change, Func<string, bool> isInside)
    {
        ArgumentNullException.ThrowIfNull(isInside);

        if (change is null) return null;

        var inside = isInside(change.NewPath);

        if (change.ChangeType != FileChangeType.Renamed || change.OldPath is null)
            return inside ? change : null;

        var wasInside = isInside(change.OldPath);

        // A rename's FileItem, when a provider supplies one, describes the file where it now is. It
        // therefore belongs to the arrival and not to the departure: naming it on the deletion would
        // hand a consumer an item whose path is the one the event says is gone.
        return (wasInside, inside) switch
        {
            (true, true) => change,
            (true, false) => new FileChangeEventArgs(FileChangeType.Deleted, change.OldPath),
            (false, true) => new FileChangeEventArgs(FileChangeType.Created, change.NewPath, change.FileItem),
            _ => null,
        };
    }

    /// <summary>
    /// Restricts <paramref name="change"/> to what <paramref name="excludedPaths"/> leaves out.
    /// </summary>
    /// <remarks>
    /// An empty list excludes nothing, so the change passes through untouched.
    /// </remarks>
    public static FileChangeEventArgs? RestrictToIncluded(FileChangeEventArgs? change, IList<string>? excludedPaths)
    {
        if (change is null) return null;
        if (excludedPaths is null || excludedPaths.Count == 0) return change;

        return Restrict(change, path => !PathExclusion.IsExcluded(path, excludedPaths));
    }
}
