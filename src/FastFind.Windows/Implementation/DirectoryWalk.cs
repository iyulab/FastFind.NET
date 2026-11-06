using System.IO.Enumeration;
using FastFind.Models;

namespace FastFind.Windows.Implementation;

/// <summary>
/// A breadth-first walk of a directory tree that holds <b>one</b> directory open at a time.
/// </summary>
/// <remarks>
/// <para>
/// <c>Directory.EnumerateFileSystemEntries</c> with <c>RecurseSubdirectories</c> opens every
/// sub-directory it discovers and keeps the handle in a queue until that directory is listed, so the
/// number of open handles follows the width of the tree: tens of thousands on a system volume. This
/// walk keeps the pending directories as <em>names</em> and lists them one by one, each with its own
/// non-recursive enumerator that is disposed before the next opens. The order is the same
/// (breadth-first, so a file cap samples the whole tree rather than one deep corner of it).
/// </para>
/// <para>
/// A directory the index would leave out (<see cref="IndexingOptions.ExcludedPaths"/>) is not
/// entered. That is the same result as listing it and discarding every entry — a path beneath an
/// excluded directory is itself excluded — without paying for the subtree.
/// </para>
/// <para>An instance walks one tree on one thread; create one per walk.</para>
/// </remarks>
internal sealed class DirectoryWalk(IndexingOptions options)
{
    private readonly IndexingOptions _options = options;

    /// <summary>The number of directories whose entries were listed. A measure of the work done.</summary>
    public int DirectoriesOpened { get; private set; }

    /// <summary>
    /// Yields the full path of every entry beneath <paramref name="root"/>, files and directories,
    /// breadth-first. Stops, without throwing, when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public IEnumerable<string> Enumerate(string root, CancellationToken cancellationToken)
    {
        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = AttributesToSkip(_options),
        };

        // How many levels below the root may be listed: depth 1 is the root's own entries, so a depth of
        // n lists n - 1 levels beneath it. null = unbounded. (The recursive enumerator this replaced was
        // handed the depth as its recursion limit, which counts steps below the root, so depth 2 and 3
        // reported one level more than they named.)
        int? levelsBelowRoot = _options.MaxDepth is { } maxDepth ? Math.Max(maxDepth - 1, 0) : null;

        var excluded = _options.ExcludedPaths;
        var followLinks = _options.FollowSymlinks;
        var pending = new Queue<(string Path, int Level, int LinkHops)>();
        pending.Enqueue((root, 0, 0));

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            var (directory, level, linkHops) = pending.Dequeue();
            var descend = levelsBelowRoot is null || level < levelsBelowRoot;

            IEnumerator<Entry> enumerator;
            try
            {
                // Opening the enumerator is what opens the directory.
                enumerator = new FileSystemEnumerable<Entry>(
                    directory,
                    static (ref FileSystemEntry entry) => new Entry(
                        entry.ToFullPath(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0),
                    enumerationOptions).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // vanished or unreadable between being queued and being listed
            }

            DirectoriesOpened++;

            // Disposed when this block ends, before the next directory opens.
            using (enumerator)
            {
                while (true)
                {
                    bool moved;
                    try { moved = enumerator.MoveNext(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }

                    if (!moved)
                        break;

                    if (cancellationToken.IsCancellationRequested)
                        yield break;

                    var entry = enumerator.Current;
                    yield return entry.Path;

                    if (!entry.IsDirectory || !descend)
                        continue;

                    if (excluded.Count > 0 && PathExclusion.IsExcluded(entry.Path, excluded))
                        continue;

                    // A junction or symbolic link is an entry like any other, but what it points at is
                    // listed where it really is. Followed only on request, and then only so far: a link
                    // back to an ancestor would otherwise walk the tree again beneath itself for ever.
                    if (entry.IsReparsePoint && (!followLinks || linkHops >= MaxLinkHops))
                        continue;

                    pending.Enqueue((entry.Path, level + 1, entry.IsReparsePoint ? linkHops + 1 : linkHops));
                }
            }
        }
    }

    private static FileAttributes AttributesToSkip(IndexingOptions options)
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

    /// <summary>How many links in a row a followed walk may cross before it stops descending.</summary>
    private const int MaxLinkHops = 8;

    private readonly record struct Entry(string Path, bool IsDirectory, bool IsReparsePoint);
}
