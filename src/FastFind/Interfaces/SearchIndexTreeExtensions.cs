using FastFind.Models;

namespace FastFind.Interfaces;

/// <summary>
/// Applies a directory's deletion or move to everything an index holds beneath it.
/// </summary>
/// <remarks>
/// A file system reports a directory rename or deletion once, for the directory. An index that
/// applied it to that one entry kept every descendant under a path that no longer exists, and never
/// showed a moved directory's contents at their new location until the next full re-index. These
/// operations carry the change down the tree through <see cref="ISearchIndex"/> alone, so they work
/// for any index.
/// </remarks>
public static class SearchIndexTreeExtensions
{
    /// <summary>
    /// Removes an entry and, when it is a directory, everything the index holds beneath it.
    /// </summary>
    /// <returns>The number of entries removed.</returns>
    public static async Task<int> RemoveTreeAsync(this ISearchIndex index, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var removed = 0;
        if (await MayHaveDescendantsAsync(index, path, cancellationToken).ConfigureAwait(false))
        {
            foreach (var descendant in await DescendantsAsync(index, path, cancellationToken).ConfigureAwait(false))
            {
                if (await index.RemoveAsync(descendant.FullPath, cancellationToken).ConfigureAwait(false))
                    removed++;
            }
        }

        if (await index.RemoveAsync(path, cancellationToken).ConfigureAwait(false))
            removed++;

        return removed;
    }

    /// <summary>
    /// Moves an entry and, when it is a directory, everything the index holds beneath it, from
    /// <paramref name="oldPath"/> to <paramref name="newPath"/>. Sizes, times and attributes are kept.
    /// </summary>
    /// <returns>The number of descendants moved, not counting the entry itself.</returns>
    public static async Task<int> MoveTreeAsync(this ISearchIndex index, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrEmpty(oldPath);
        ArgumentException.ThrowIfNullOrEmpty(newPath);

        var root = await index.GetAsync(oldPath, cancellationToken).ConfigureAwait(false);
        var descendants = root is { IsDirectory: false }
            ? []
            : await DescendantsAsync(index, oldPath, cancellationToken).ConfigureAwait(false);

        var moved = new List<FastFileItem>(descendants.Count + 1);
        foreach (var item in descendants)
        {
            await index.RemoveAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
            moved.Add(Rebase(item, oldPath, newPath));
        }

        if (root is { } entry)
        {
            await index.RemoveAsync(oldPath, cancellationToken).ConfigureAwait(false);
            moved.Add(Rebase(entry, oldPath, newPath));
        }

        if (moved.Count > 0)
            await index.AddBatchAsync(moved, cancellationToken).ConfigureAwait(false);

        return descendants.Count;
    }

    /// <summary>
    /// A file cannot have descendants; an entry the index does not hold might still be a
    /// directory whose own entry was never indexed, so it is checked.
    /// </summary>
    private static async Task<bool> MayHaveDescendantsAsync(ISearchIndex index, string path, CancellationToken cancellationToken)
    {
        var entry = await index.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return entry is null || entry.Value.IsDirectory;
    }

    private static async Task<List<FastFileItem>> DescendantsAsync(ISearchIndex index, string directory, CancellationToken cancellationToken)
    {
        // Materialised before anything is removed: no backend promises a stable enumeration of a
        // collection it is being asked to change.
        var items = new List<FastFileItem>();
        await foreach (var item in index.GetByDirectoryAsync(directory, recursive: true, cancellationToken).ConfigureAwait(false))
        {
            // The contract says "beneath"; hold every backend to a separator boundary, so a sibling
            // sharing the prefix (App vs App.Tests) is never swept in.
            if (SearchQueryEvaluator.IsUnder(item.DirectoryPath, directory, includeSubdirectories: true))
                items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// The same entry with <paramref name="oldRoot"/> swapped for <paramref name="newRoot"/> at the
    /// front of its path — which also gives the moved directory itself its new name and parent.
    /// </summary>
    private static FastFileItem Rebase(FastFileItem item, string oldRoot, string newRoot)
    {
        var fullPath = TrimSeparators(newRoot) + item.FullPath[Math.Min(TrimSeparators(oldRoot).Length, item.FullPath.Length)..];

        return new FastFileItem(
            fullPath,
            Path.GetFileName(fullPath),
            Path.GetDirectoryName(fullPath) ?? item.DirectoryPath,
            item.IsDirectory ? string.Empty : Path.GetExtension(fullPath),
            item.Size,
            item.CreatedTime,
            item.ModifiedTime,
            item.AccessedTime,
            item.Attributes,
            item.DriveLetter,
            item.FileRecordNumber);
    }

    private static string TrimSeparators(string path) => path.TrimEnd('\\', '/');
}
