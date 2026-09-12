using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers;

namespace FastFind.Models;

/// <summary>
/// High-performance string interning pool.
/// </summary>
/// <remarks>
/// <para>
/// The pool holds a single mapping per unique string: one dictionary entry for the
/// string-to-id direction, and one slot in a growable array for the id-to-string direction.
/// Ids are dense and assigned in allocation order, which makes reverse lookup an array index
/// rather than a hash lookup.
/// </para>
/// <para>
/// Deduplication is exact (ordinal). Case is never folded into the key: two paths that differ
/// only in case are distinct entries, because on case-sensitive file systems they are distinct
/// files and folding them would make one of them unreachable through <see cref="Get"/>.
/// </para>
/// <para>
/// Interned ids remain valid for the lifetime of the process. Entries are never evicted, since
/// any live value holding an id would otherwise silently resolve to an empty string.
/// </para>
/// </remarks>
public static class StringPool
{
    /// <summary>Id reserved for the empty string.</summary>
    private const int EmptyId = 0;

    /// <summary>
    /// Approximate per-entry overhead beyond the character data: string object header and
    /// padding, one dictionary node, and one array slot.
    /// </summary>
    private const int PerEntryOverheadBytes = 70;

    private const int InitialCapacity = 1024;

    private static readonly ConcurrentDictionary<string, int> _stringToId = new(StringComparer.Ordinal);
    private static ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>>? _spanLookup;

    // Reverse mapping: _values[id] is the string interned under that id. Index 0 is unused.
    private static string?[] _values = new string?[InitialCapacity];
    private static readonly Lock _valuesLock = new();

    private static readonly Lock _statsLock = new();
    private static long _internedCount;
    private static long _memoryBytes;
    private static int _nextId;

    // Per-kind counters reported by GetStats, incremented once per newly interned string.
    private static int _pathCount;
    private static int _extensionCount;
    private static int _nameCount;

    private static long _hitCount;
    private static long _missCount;

    private static readonly SearchValues<char> _pathSeparators = SearchValues.Create(['/', '\\']);

    /// <summary>
    /// Interns a string and returns its id. Repeated calls with an equal string return the same id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Intern(string value) => Intern(value, out _);

    /// <summary>
    /// Interns a string, also reporting whether this call was the one that created the entry.
    /// </summary>
    private static int Intern(string value, out bool created)
    {
        created = false;

        if (string.IsNullOrEmpty(value))
            return EmptyId;

        if (_stringToId.TryGetValue(value, out var existingId))
        {
            Interlocked.Increment(ref _hitCount);
            return existingId;
        }

        Interlocked.Increment(ref _missCount);

        var newId = Interlocked.Increment(ref _nextId);

        // Publish the value before the id becomes reachable, so an id handed out by the
        // dictionary always resolves. A lost race leaves an unreferenced slot, which is harmless.
        StoreValue(newId, value);

        var actualId = _stringToId.GetOrAdd(value, newId);
        if (actualId == newId)
        {
            created = true;
            Interlocked.Increment(ref _internedCount);
            Interlocked.Add(ref _memoryBytes, (value.Length * 2) + PerEntryOverheadBytes);
        }

        return actualId;
    }

    /// <summary>
    /// Resolves an id back to its string. Returns <see cref="string.Empty"/> for id 0 or an
    /// unknown id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Get(int id)
    {
        if (id <= EmptyId) return string.Empty;

        var values = Volatile.Read(ref _values);
        if ((uint)id < (uint)values.Length)
        {
            var value = Volatile.Read(ref values[id]);
            if (value is not null) return value;
        }

        // Rare: the array grew between the id being assigned and this read. Re-read under the
        // growth lock, which is the only writer of the array reference.
        lock (_valuesLock)
        {
            values = _values;
            return (uint)id < (uint)values.Length ? values[id] ?? string.Empty : string.Empty;
        }
    }

    /// <summary>
    /// Alias for <see cref="Get"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetString(int id) => Get(id);

    /// <summary>
    /// Zero-allocation span view over an interned string.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> GetSpan(int id) => Get(id).AsSpan();

    /// <summary>
    /// Zero-allocation memory view over an interned string, for async scenarios.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlyMemory<char> GetMemory(int id) => Get(id).AsMemory();

    /// <summary>
    /// Span view over an interned string, reporting whether the id was known.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetSpan(int id, out ReadOnlySpan<char> span)
    {
        if (id == EmptyId)
        {
            span = ReadOnlySpan<char>.Empty;
            return true; // id 0 is a valid id for the empty string
        }

        if (id > EmptyId)
        {
            var values = Volatile.Read(ref _values);
            if ((uint)id < (uint)values.Length)
            {
                var value = Volatile.Read(ref values[id]);
                if (value is not null)
                {
                    span = value.AsSpan();
                    return true;
                }
            }
        }

        span = ReadOnlySpan<char>.Empty;
        return false;
    }

    /// <summary>
    /// Interns a file-system path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is interned <b>verbatim</b>: neither its case nor its separators are rewritten. A
    /// pool stores values, and a value a caller cannot get back is a value it did not store.
    /// </para>
    /// <para>
    /// This folded <c>/</c> to <c>\</c> on Windows until 2.4.0, for deduplication. It bought
    /// nothing measurable — every Windows enumerator builds its items through
    /// <see cref="FileInfo"/>/<see cref="DirectoryInfo"/>, which canonicalise separators before the
    /// path ever reaches this method, so the fold only ever reached directly constructed items —
    /// and it cost those items their spelling. Separator-insensitive <i>identity</i> on Windows
    /// comes from the comparers (<c>FileEntryTable.PathKeyComparer</c>) and from
    /// <see cref="PathExclusion"/>, which is where it belongs; see the path-normalization note in
    /// the repository guide.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InternPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return EmptyId;

        return InternCounted(path, ref _pathCount);
    }

    /// <summary>
    /// Interns a file extension, normalized to lower case so that extension comparisons stay
    /// case-insensitive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InternExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return EmptyId;

        // Allocates only when the extension is not already lower case.
        if (!IsLowerInvariant(extension))
        {
            extension = string.Create(extension.Length, extension, static (span, ext) =>
            {
                ext.AsSpan().ToLowerInvariant(span);
            });
        }

        return InternCounted(extension, ref _extensionCount);
    }

    /// <summary>
    /// Interns a file name. Case is preserved.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InternName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return EmptyId;

        return InternCounted(name, ref _nameCount);
    }

    /// <summary>
    /// Interns a string given as a span, allocating only when it is not already pooled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InternFromSpan(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return EmptyId;

        var lookup = GetSpanLookup();
        if (lookup.TryGetValue(value, out var existingId))
        {
            Interlocked.Increment(ref _hitCount);
            return existingId;
        }

        return Intern(new string(value));
    }

    /// <summary>
    /// Looks up the id of an already-interned string without allocating. Returns false when the
    /// string has not been interned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetFromSpan(ReadOnlySpan<char> value, out int id)
    {
        if (value.IsEmpty)
        {
            id = EmptyId;
            return true; // empty is always "found" as id 0
        }

        return GetSpanLookup().TryGetValue(value, out id);
    }

    /// <summary>
    /// Splits a full path and interns its directory, name and extension in one pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int directoryId, int nameId, int extensionId) InternPathComponents(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return (0, 0, 0);

        var span = fullPath.AsSpan();
        var lastSeparator = span.LastIndexOfAny(_pathSeparators);

        ReadOnlySpan<char> directorySpan;
        ReadOnlySpan<char> nameSpan;

        if (lastSeparator >= 0)
        {
            directorySpan = span[..lastSeparator];
            nameSpan = span[(lastSeparator + 1)..];
        }
        else
        {
            directorySpan = ReadOnlySpan<char>.Empty;
            nameSpan = span;
        }

        var lastDot = nameSpan.LastIndexOf('.');
        ReadOnlySpan<char> extensionSpan = lastDot >= 0 ? nameSpan[lastDot..] : ReadOnlySpan<char>.Empty;

        var directoryId = directorySpan.IsEmpty ? 0 : InternPath(new string(directorySpan));
        var nameId = nameSpan.IsEmpty ? 0 : InternName(new string(nameSpan));
        var extensionId = extensionSpan.IsEmpty ? 0 : InternExtension(new string(extensionSpan));

        return (directoryId, nameId, extensionId);
    }

    /// <summary>
    /// Current pool statistics.
    /// </summary>
    public static StringPoolStats GetStats()
    {
        return new StringPoolStats(
            Interlocked.Read(ref _internedCount),
            Interlocked.Read(ref _memoryBytes),
            Volatile.Read(ref _pathCount),
            Volatile.Read(ref _extensionCount),
            Volatile.Read(ref _nameCount),
            _stringToId.Count
        );
    }

    /// <summary>
    /// No longer removes entries.
    /// </summary>
    /// <remarks>
    /// Evicting pooled strings invalidates ids that live values still hold, after which every
    /// affected path, name and extension silently resolves to an empty string. There is no way to
    /// evict safely while ids are held by callers, so this method does nothing. Use
    /// <see cref="Reset"/> when no interned value is still in use.
    /// </remarks>
    [Obsolete("Cleanup no longer evicts entries: eviction invalidates ids held by live values. Use Reset() when no interned value is still in use.")]
    public static void Cleanup()
    {
    }

    /// <summary>
    /// Forces a compacting garbage collection.
    /// </summary>
    public static void CompactMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    /// <summary>
    /// Clears the pool. Every previously issued id becomes invalid, so this is only safe when no
    /// interned value is still in use.
    /// </summary>
    public static void Reset()
    {
        lock (_statsLock)
        {
            lock (_valuesLock)
            {
                _spanLookup = null;
                _stringToId.Clear();
                _values = new string?[InitialCapacity];
            }

            Interlocked.Exchange(ref _internedCount, 0);
            Interlocked.Exchange(ref _memoryBytes, 0);
            Interlocked.Exchange(ref _nextId, 0);
            Interlocked.Exchange(ref _hitCount, 0);
            Interlocked.Exchange(ref _missCount, 0);
            Volatile.Write(ref _pathCount, 0);
            Volatile.Write(ref _extensionCount, 0);
            Volatile.Write(ref _nameCount, 0);
        }
    }

    /// <summary>
    /// Pool statistics together with process-level GC counters.
    /// </summary>
    public static StringPoolAdvancedStats GetAdvancedStats()
    {
        var basicStats = GetStats();

        return new StringPoolAdvancedStats(
            basicStats,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalMemory(false),
            CalculateFragmentationRatio(),
            CalculateHitRatio()
        );
    }

    /// <summary>
    /// Interns a value and attributes a newly created entry to one of the per-kind counters.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InternCounted(string value, ref int kindCounter)
    {
        var id = Intern(value, out var created);

        if (created)
            Interlocked.Increment(ref kindCounter);

        return id;
    }

    /// <summary>
    /// Writes a value into the reverse-mapping array, growing it when needed.
    /// </summary>
    private static void StoreValue(int id, string value)
    {
        var values = Volatile.Read(ref _values);

        if ((uint)id >= (uint)values.Length)
        {
            lock (_valuesLock)
            {
                values = _values;
                if ((uint)id >= (uint)values.Length)
                {
                    var capacity = values.Length;
                    while (capacity <= id) capacity *= 2;

                    var grown = new string?[capacity];
                    Array.Copy(values, grown, values.Length);
                    Volatile.Write(ref _values, grown);
                    values = grown;
                }

                Volatile.Write(ref values[id], value);
                return;
            }
        }

        Volatile.Write(ref values[id], value);
    }

    /// <summary>
    /// Cached alternate lookup, so span-keyed reads never allocate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> GetSpanLookup()
    {
        var lookup = _spanLookup;
        if (lookup.HasValue)
            return lookup.Value;

        lock (_valuesLock)
        {
            lookup = _spanLookup;
            if (lookup.HasValue)
                return lookup.Value;

            var created = _stringToId.GetAlternateLookup<ReadOnlySpan<char>>();
            _spanLookup = created;
            return created;
        }
    }

    private static bool IsLowerInvariant(string value)
    {
        foreach (var c in value)
        {
            if (char.IsUpper(c)) return false;
        }

        return true;
    }

    private static double CalculateFragmentationRatio()
    {
        var issued = Volatile.Read(ref _nextId);
        if (issued == 0) return 0;

        // Ids lost to concurrent insertion races leave unreferenced slots in the reverse array.
        return 1.0 - ((double)Interlocked.Read(ref _internedCount) / issued);
    }

    private static double CalculateHitRatio()
    {
        var hits = Interlocked.Read(ref _hitCount);
        var misses = Interlocked.Read(ref _missCount);
        var total = hits + misses;

        return total > 0 ? (double)hits / total : 0;
    }
}

/// <summary>
/// StringPool statistics.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct StringPoolStats(long internedCount, long memoryUsageBytes, int pathPoolSize,
                                      int extensionPoolSize, int namePoolSize, int totalPoolSize)
{
    public readonly long InternedCount = internedCount;

    /// <summary>
    /// Estimated retained bytes: character data plus per-entry object, dictionary and array
    /// overhead. An estimate, not a measurement.
    /// </summary>
    public readonly long MemoryUsageBytes = memoryUsageBytes;

    /// <summary>Distinct strings interned through <see cref="StringPool.InternPath"/>.</summary>
    public readonly int PathPoolSize = pathPoolSize;

    /// <summary>Distinct strings interned through <see cref="StringPool.InternExtension"/>.</summary>
    public readonly int ExtensionPoolSize = extensionPoolSize;

    /// <summary>Distinct strings interned through <see cref="StringPool.InternName"/>.</summary>
    public readonly int NamePoolSize = namePoolSize;

    /// <summary>Total distinct strings in the pool.</summary>
    public readonly int TotalPoolSize = totalPoolSize;

    public double MemoryUsageMB => MemoryUsageBytes / (1024.0 * 1024.0);

    public double AverageStringLength => InternedCount > 0 ? (double)MemoryUsageBytes / (InternedCount * 2) : 0;

    /// <summary>
    /// Always 0: the pool stores one entry per distinct string, so there is no duplication left
    /// to report here. Deduplication effectiveness is
    /// <see cref="StringPoolAdvancedStats.HitRatio"/>.
    /// </summary>
    public double CompressionRatio => TotalPoolSize > 0 ? 1.0 - ((double)InternedCount / TotalPoolSize) : 0;
}

/// <summary>
/// StringPool statistics together with process-level GC counters.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct StringPoolAdvancedStats(StringPoolStats basicStats, int gen0Collections, int gen1Collections,
                                             int gen2Collections, long totalMemory, double fragmentationRatio, double hitRatio)
{
    public readonly StringPoolStats BasicStats = basicStats;
    public readonly int Gen0Collections = gen0Collections;
    public readonly int Gen1Collections = gen1Collections;
    public readonly int Gen2Collections = gen2Collections;
    public readonly long TotalMemory = totalMemory;
    public readonly double FragmentationRatio = fragmentationRatio;
    public readonly double HitRatio = hitRatio;

    public double MemoryEfficiency => 1.0 - FragmentationRatio;
    public double TotalMemoryMB => TotalMemory / (1024.0 * 1024.0);
}
