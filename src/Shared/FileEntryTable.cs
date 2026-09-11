using System.Collections.Concurrent;
using FastFind.Models;

namespace FastFind.Indexing;

/// <summary>
/// The in-memory index's storage: every entry, grouped by the directory that holds it.
/// </summary>
/// <remarks>
/// <para>
/// Compiled into each platform package from one source file, so both hold the same definition
/// without a public type or internals shared across packages that version separately.
/// </para>
/// <para>
/// Entries are held as <see cref="FastFileItem"/> — the library's compact form — in a map from
/// directory to a map from name to item. Both keys are the item's own
/// <see cref="FastFileItem.DirectoryPath"/> and <see cref="FastFileItem.Name"/> instances, so the
/// table adds no string of its own per entry: no full-path key, no lower-cased copy.
/// </para>
/// <para>
/// Identity follows the host file system through the comparers, never by rewriting a key: on Windows
/// <c>C:\Data</c>, <c>c:\data</c>, <c>C:/data</c> and <c>C:\data\</c> are one directory. A lookup by
/// full path splits the path at its last separator and looks both halves up as spans, so reads
/// allocate nothing.
/// </para>
/// <para>
/// A subtree is answered from the directory keys with <see cref="SearchQueryEvaluator.IsUnder"/>.
/// Directories are a small fraction of the entries, so this replaces a per-file trie that cost more
/// memory than everything else in the index together.
/// </para>
/// <para>
/// Reads are lock-free and may run concurrently with writes; writes are serialised internally.
/// </para>
/// </remarks>
internal sealed class FileEntryTable
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FastFileItem>> _directories =
        new(PathKeyComparer.Instance);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FastFileItem>>.AlternateLookup<ReadOnlySpan<char>> _directoryLookup;
    private readonly object _writeLock = new();
    private long _count;

    public FileEntryTable()
    {
        _directoryLookup = _directories.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Comparer for names within one directory.</summary>
    private static StringComparer NameComparer { get; } =
        StringComparer.FromComparison(SearchQueryEvaluator.PathComparison);

    public long Count => Interlocked.Read(ref _count);

    /// <summary>Number of distinct directories holding at least one entry.</summary>
    public int DirectoryCount => _directories.Count;

    /// <summary>Adds an entry unless one with the same path exists.</summary>
    /// <returns><see langword="true"/> if it was added.</returns>
    public bool TryAdd(in FastFileItem item) => Put(item, replace: false, out _);

    /// <summary>Adds an entry, replacing one with the same path.</summary>
    /// <param name="item">Entry to store.</param>
    /// <param name="previous">The entry it replaced, when there was one.</param>
    /// <returns><see langword="true"/> if an entry was replaced.</returns>
    public bool Set(in FastFileItem item, out FastFileItem previous)
    {
        Put(item, replace: true, out var replaced);
        previous = replaced ?? default;
        return replaced.HasValue;
    }

    public bool TryGet(string fullPath, out FastFileItem item)
    {
        item = default;
        return Split(fullPath, out var directory, out var name)
               && _directoryLookup.TryGetValue(directory, out var entries)
               && entries.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out item);
    }

    public bool Contains(string fullPath) => TryGet(fullPath, out _);

    public bool TryRemove(string fullPath, out FastFileItem removed)
    {
        removed = default;
        if (!Split(fullPath, out var directory, out var name)) return false;

        lock (_writeLock)
        {
            if (!_directoryLookup.TryGetValue(directory, out var directoryKey, out var entries)) return false;
            if (!entries.GetAlternateLookup<ReadOnlySpan<char>>().TryRemove(name, out _, out removed)) return false;

            if (entries.IsEmpty) _directories.TryRemove(directoryKey, out _);
            Interlocked.Decrement(ref _count);
            return true;
        }
    }

    public void Clear()
    {
        lock (_writeLock)
        {
            _directories.Clear();
            Interlocked.Exchange(ref _count, 0);
        }
    }

    /// <summary>Every entry.</summary>
    /// <remarks>
    /// Enumerates the dictionaries themselves, never their <c>Values</c>: on a concurrent
    /// dictionary that property takes every lock and copies the whole collection, which for a
    /// search is a copy of the index per query.
    /// </remarks>
    public IEnumerable<FastFileItem> All()
    {
        foreach (var directory in _directories)
        {
            foreach (var entry in directory.Value) yield return entry.Value;
        }
    }

    /// <summary>Entries directly inside <paramref name="directory"/>.</summary>
    public IEnumerable<FastFileItem> InDirectory(string directory)
    {
        return _directoryLookup.TryGetValue(TrimTrailingSeparators(directory), out var entries)
            ? Values(entries)
            : [];
    }

    private static IEnumerable<FastFileItem> Values(ConcurrentDictionary<string, FastFileItem> entries)
    {
        foreach (var entry in entries) yield return entry.Value;
    }

    /// <summary>Entries at or beneath <paramref name="root"/>, at any depth.</summary>
    public IEnumerable<FastFileItem> Under(string root)
    {
        foreach (var (directory, entries) in _directories)
        {
            if (!SearchQueryEvaluator.IsUnder(directory, root, includeSubdirectories: true)) continue;
            foreach (var entry in entries) yield return entry.Value;
        }
    }

    /// <summary>Whether any entry sits at or beneath <paramref name="path"/>.</summary>
    public bool Covers(string path)
    {
        foreach (var directory in _directories)
        {
            if (SearchQueryEvaluator.IsUnder(directory.Key, path, includeSubdirectories: true)) return true;
        }

        return false;
    }

    /// <summary>The directories holding entries, as stored.</summary>
    public IEnumerable<string> Directories => _directories.Select(directory => directory.Key);

    private bool Put(in FastFileItem item, bool replace, out FastFileItem? replaced)
    {
        replaced = null;
        var (directoryKey, nameKey) = KeysFor(item);

        lock (_writeLock)
        {
            var entries = _directories.GetOrAdd(directoryKey,
                static _ => new ConcurrentDictionary<string, FastFileItem>(concurrencyLevel: 1, capacity: 1, NameComparer));

            if (entries.TryGetValue(nameKey, out var existing))
            {
                if (!replace) return false;
                // Keyed under the name it was first stored with; a case-only rename keeps the key
                // and takes the new item, which carries the new spelling.
                entries[nameKey] = item;
                replaced = existing;
                return true;
            }

            entries[nameKey] = item;
            Interlocked.Increment(ref _count);
            return true;
        }
    }

    /// <summary>
    /// The keys an item is stored under: the ones a lookup by its full path will compute.
    /// </summary>
    /// <remarks>
    /// Normally the item's own directory and name instances, so storing adds no string. A provider
    /// whose directory or name does not agree with its full path would otherwise store an entry that
    /// no lookup by that path can reach; such an entry is keyed by the split of its full path.
    /// </remarks>
    private static (string Directory, string Name) KeysFor(in FastFileItem item)
    {
        var directory = item.DirectoryPath;
        var name = item.Name;
        if (!Split(item.FullPath, out var splitDirectory, out var splitName))
        {
            return (directory, name);
        }

        return (PathKeyComparer.Instance.Equals(splitDirectory, directory) ? directory : new string(splitDirectory),
                splitName.Equals(name, SearchQueryEvaluator.PathComparison) ? name : new string(splitName));
    }

    /// <summary>
    /// Splits a full path into the directory and name an entry is stored under.
    /// </summary>
    private static bool Split(string fullPath, out ReadOnlySpan<char> directory, out ReadOnlySpan<char> name)
    {
        var path = TrimTrailingSeparators(fullPath.AsSpan());
        var cut = OperatingSystem.IsWindows() ? path.LastIndexOfAny('\\', '/') : path.LastIndexOf('/');

        if (cut < 0 || path.IsEmpty)
        {
            directory = default;
            name = default;
            return false;
        }

        directory = path[..cut];
        name = path[(cut + 1)..];
        return !name.IsEmpty;
    }

    private static ReadOnlySpan<char> TrimTrailingSeparators(ReadOnlySpan<char> path) =>
        OperatingSystem.IsWindows() ? path.TrimEnd(['\\', '/']) : path.TrimEnd('/');

    private static ReadOnlySpan<char> TrimTrailingSeparators(string path) => TrimTrailingSeparators(path.AsSpan());

    /// <summary>
    /// Directory identity on the host file system: trailing separators ignored; on Windows,
    /// case-insensitive with <c>/</c> and <c>\</c> equivalent.
    /// </summary>
    internal sealed class PathKeyComparer : IEqualityComparer<string>, IAlternateEqualityComparer<ReadOnlySpan<char>, string>
    {
        public static PathKeyComparer Instance { get; } = new();

        private static readonly bool FoldsCaseAndSeparators = OperatingSystem.IsWindows();

        public bool Equals(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return Equals(x.AsSpan(), y);
        }

        public int GetHashCode(string obj) => GetHashCode(obj.AsSpan());

        public bool Equals(ReadOnlySpan<char> alternate, string other)
        {
            var a = TrimTrailingSeparators(alternate);
            var b = TrimTrailingSeparators(other.AsSpan());
            if (a.Length != b.Length) return false;
            if (!FoldsCaseAndSeparators) return a.SequenceEqual(b);
            if (a.IndexOf('/') < 0 && b.IndexOf('/') < 0) return a.Equals(b, StringComparison.OrdinalIgnoreCase);

            // Rare: a path spelled with forward slashes. Compare separator by separator, and every
            // run between them with the same case rule the hash uses.
            while (true)
            {
                var cut = a.IndexOfAny('\\', '/');
                if (cut < 0) return a.Equals(b, StringComparison.OrdinalIgnoreCase);
                if (b[cut] is not ('\\' or '/')) return false;
                if (!a[..cut].Equals(b[..cut], StringComparison.OrdinalIgnoreCase)) return false;
                a = a[(cut + 1)..];
                b = b[(cut + 1)..];
            }
        }

        public int GetHashCode(ReadOnlySpan<char> alternate)
        {
            var span = TrimTrailingSeparators(alternate);
            if (!FoldsCaseAndSeparators) return string.GetHashCode(span, StringComparison.Ordinal);
            if (span.IndexOf('/') < 0) return string.GetHashCode(span, StringComparison.OrdinalIgnoreCase);

            // Hash a forward-slash spelling as if written with backslashes.
            char[]? rented = null;
            var buffer = span.Length <= 256
                ? stackalloc char[span.Length]
                : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(span.Length)).AsSpan(0, span.Length);
            span.Replace(buffer, '/', '\\');
            var hash = string.GetHashCode(buffer, StringComparison.OrdinalIgnoreCase);
            if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
            return hash;
        }

        public string Create(ReadOnlySpan<char> alternate) => new(alternate);
    }
}
