using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace FastFind.Models;

/// <summary>
/// Ultra-high performance file item using struct and string interning for maximum memory efficiency
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct FastFileItem
{
    /// <summary>
    /// How <see cref="FullPath"/> is produced: a <see cref="StringPool"/> id when it is at least
    /// zero, otherwise one of the <c>Derived…</c> values below.
    /// </summary>
    /// <remarks>
    /// The full path is unique per file, so interning it deduplicates nothing, and it is the
    /// largest string an item has. Almost always it is exactly the directory, a separator and the
    /// name — each of which is shared or needed anyway — so it is rebuilt from them on read instead
    /// of being kept. Only a path that is not that composition is interned, which keeps
    /// <see cref="FullPath"/> verbatim in every case.
    /// </remarks>
    private readonly int _fullPathId;
    private readonly int _nameId;
    private readonly int _directoryPathId;
    private readonly int _extensionId;

    /// <summary>Directory, <c>\</c>, name.</summary>
    private const int DerivedWithBackslash = -1;

    /// <summary>Directory, <c>/</c>, name.</summary>
    private const int DerivedWithSlash = -2;

    /// <summary>Directory then name — the directory already ends in a separator, or is empty.</summary>
    private const int DerivedByConcatenation = -3;

    /// <summary>
    /// A <see cref="StringPool"/> id for <see cref="FullPath"/>.
    /// </summary>
    /// <remarks>
    /// The full path is no longer interned when it can be rebuilt from the directory and name, so
    /// reading this interns it on demand — putting back, for this item, the memory the change saves.
    /// </remarks>
    [Obsolete("The full path is rebuilt from DirectoryId and NameId rather than interned. Reading this interns it on demand; use FullPath, or DirectoryId and NameId.")]
    public int FullPathId => _fullPathId >= 0 ? _fullPathId : StringPool.InternPath(FullPath);

    public int NameId => _nameId;
    public int DirectoryId => _directoryPathId;
    public int ExtensionId => _extensionId;

    // 원시 타입으로 최대 성능
    /// <summary>
    /// Size in bytes. Also 0 when the size was not collected — the Windows MFT provider reads it
    /// only when <see cref="IndexingOptions.CollectFileMetadata"/> is set.
    /// </summary>
    public readonly long Size;
    public readonly long CreatedTicks;
    public readonly long ModifiedTicks;
    public readonly long AccessedTicks;
    public readonly FileAttributes Attributes;
    public readonly char DriveLetter;
    public readonly ulong FileRecordNumber;

    public FastFileItem(string fullPath, string name, string directoryPath, string extension,
                       long size, DateTime created, DateTime modified, DateTime accessed,
                       FileAttributes attributes, char driveLetter, ulong? fileRecordNumber = null)
    {
        _nameId = StringPool.InternName(name);
        _directoryPathId = StringPool.InternPath(directoryPath);
        _extensionId = StringPool.InternExtension(extension);
        _fullPathId = HowToBuild(fullPath, StringPool.Get(_directoryPathId), StringPool.Get(_nameId));

        Size = size;
        CreatedTicks = ToUtcTicks(created);
        ModifiedTicks = ToUtcTicks(modified);
        AccessedTicks = ToUtcTicks(accessed);
        Attributes = attributes;
        DriveLetter = driveLetter;
        FileRecordNumber = fileRecordNumber ?? 0;
    }

    /// <summary>
    /// The item's path, exactly as it was given.
    /// </summary>
    /// <remarks>
    /// Rebuilt from <see cref="DirectoryPath"/> and <see cref="Name"/> on each read, so each read
    /// allocates. Read it once into a local in a loop that uses it more than once.
    /// </remarks>
    public string FullPath => _fullPathId >= 0
        ? StringPool.Get(_fullPathId)
        : string.Create(FullPathLength, this, static (destination, item) => item.CopyFullPathTo(destination));

    /// <summary>Length of <see cref="FullPath"/>, without building it.</summary>
    internal int FullPathLength => _fullPathId switch
    {
        >= 0 => StringPool.Get(_fullPathId).Length,
        DerivedByConcatenation => DirectoryPath.Length + Name.Length,
        _ => DirectoryPath.Length + 1 + Name.Length,
    };

    /// <summary>
    /// Writes <see cref="FullPath"/> into <paramref name="destination"/>, which must hold
    /// <see cref="FullPathLength"/> characters, without allocating.
    /// </summary>
    internal void CopyFullPathTo(Span<char> destination)
    {
        if (_fullPathId >= 0)
        {
            StringPool.Get(_fullPathId).AsSpan().CopyTo(destination);
            return;
        }

        var directory = DirectoryPath;
        var name = Name;
        directory.AsSpan().CopyTo(destination);
        var at = directory.Length;
        if (_fullPathId != DerivedByConcatenation)
        {
            destination[at++] = _fullPathId == DerivedWithBackslash ? '\\' : '/';
        }

        name.AsSpan().CopyTo(destination[at..]);
    }

    /// <summary>
    /// Runs <paramref name="use"/> over <see cref="FullPath"/>'s characters without allocating a
    /// string for them.
    /// </summary>
    internal TResult WithFullPath<TState, TResult>(TState state, FullPathFunc<TState, TResult> use)
        where TState : allows ref struct
    {
        if (_fullPathId >= 0) return use(StringPool.Get(_fullPathId), state);

        var length = FullPathLength;
        char[]? rented = null;
        var buffer = length <= 512
            ? stackalloc char[512]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(length));
        try
        {
            var path = buffer[..length];
            CopyFullPathTo(path);
            return use(path, state);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    internal delegate TResult FullPathFunc<TState, out TResult>(ReadOnlySpan<char> fullPath, TState state)
        where TState : allows ref struct;

    /// <summary>
    /// The <see cref="_fullPathId"/> for a path: a <c>Derived…</c> value when the path is exactly its
    /// directory and name composed, otherwise the interned path.
    /// </summary>
    /// <param name="fullPath">The path as given.</param>
    /// <param name="directoryPath">The directory as the pool stores it.</param>
    /// <param name="name">The name as the pool stores it.</param>
    /// <remarks>
    /// Compared against the pooled parts, and with separators folded the way the pool folds a path
    /// on Windows, so a rebuilt path reads back exactly as an interned one would have.
    /// </remarks>
    private static int HowToBuild(string fullPath, string directoryPath, string name)
    {
        var stored = OperatingSystem.IsWindows() && fullPath.Contains('/') ? fullPath.Replace('/', '\\') : fullPath;
        var path = stored.AsSpan();
        if (path.StartsWith(directoryPath, StringComparison.Ordinal)
            && path.EndsWith(name, StringComparison.Ordinal))
        {
            if (path.Length == directoryPath.Length + name.Length)
            {
                return DerivedByConcatenation;
            }

            if (path.Length == directoryPath.Length + 1 + name.Length)
            {
                switch (path[directoryPath.Length])
                {
                    case '\\': return DerivedWithBackslash;
                    case '/': return DerivedWithSlash;
                }
            }
        }

        return StringPool.InternPath(fullPath);
    }

    public string Name
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StringPool.Get(_nameId);
    }

    public string DirectoryPath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StringPool.Get(_directoryPathId);
    }

    public string Extension
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StringPool.Get(_extensionId);
    }

    /// <summary>
    /// Creation time in UTC, or <see cref="DateTime.MinValue"/> when it was not collected.
    /// </summary>
    public DateTime CreatedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(CreatedTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Last write time in UTC, or <see cref="DateTime.MinValue"/> when it was not collected.
    /// </summary>
    /// <remarks>
    /// The Windows MFT provider collects timestamps only when
    /// <see cref="IndexingOptions.CollectFileMetadata"/> is set.
    /// </remarks>
    public DateTime ModifiedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(ModifiedTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Last access time in UTC, or <see cref="DateTime.MinValue"/> when it was not collected.
    /// </summary>
    public DateTime AccessedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(AccessedTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Converts a timestamp to UTC ticks, keeping <see cref="DateTime.MinValue"/> — "not
    /// collected" — as itself.
    /// </summary>
    /// <remarks>
    /// A local <see cref="DateTime.MinValue"/> would otherwise convert to a few hours past it
    /// wherever the UTC offset is negative, and stop reading as unset.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ToUtcTicks(DateTime value) =>
        value.Ticks == 0 || value.Kind == DateTimeKind.Utc ? value.Ticks : value.ToUniversalTime().Ticks;

    // 비트 연산으로 최대 성능
    public bool IsDirectory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & FileAttributes.Directory) != 0;
    }

    public bool IsHidden
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & FileAttributes.Hidden) != 0;
    }

    public bool IsSystem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & FileAttributes.System) != 0;
    }

    public bool IsReadOnly
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & FileAttributes.ReadOnly) != 0;
    }

    public bool IsArchive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & FileAttributes.Archive) != 0;
    }

    public bool IsCompressed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & FileAttributes.Compressed) != 0;
    }

    public bool IsEncrypted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Attributes & FileAttributes.Encrypted) != 0;
    }

    // Lazy 표시용 속성들 - 캐시 최적화
    public string SizeFormatted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => LazyFormatCache.GetSizeFormatted(Size);
    }

    public string FileType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IsDirectory ? "Folder" : LazyFormatCache.GetFileTypeDescription(Extension);
    }

    // SIMD 최적화된 검색 메서드들
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesName(string searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(Name.AsSpan(), searchTerm.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesName(ReadOnlySpan<char> searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(Name.AsSpan(), searchTerm);
    }

    public bool MatchesPath(string searchTerm)
    {
        return WithFullPath(searchTerm, static (path, term) => SIMDStringMatcher.ContainsVectorized(path, term.AsSpan()));
    }

    public bool MatchesPath(ReadOnlySpan<char> searchTerm)
    {
        return WithFullPath(searchTerm, static (path, term) => SIMDStringMatcher.ContainsVectorized(path, term));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesExtension(string searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(Extension.AsSpan(), searchTerm.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesExtension(ReadOnlySpan<char> searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(Extension.AsSpan(), searchTerm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesWildcard(string pattern)
    {
        return SIMDStringMatcher.MatchesWildcard(Name.AsSpan(), pattern.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesWildcard(ReadOnlySpan<char> pattern)
    {
        return SIMDStringMatcher.MatchesWildcard(Name.AsSpan(), pattern);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesAny(string searchTerm)
    {
        return MatchesName(searchTerm) || MatchesPath(searchTerm);
    }

    // 고성능 필터링 메서드들
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAttributes(FileAttributes attrs)
    {
        return (Attributes & attrs) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAnyAttributes(FileAttributes attrs)
    {
        return (Attributes & attrs) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAllAttributes(FileAttributes attrs)
    {
        return (Attributes & attrs) == attrs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLargerThan(long sizeBytes)
    {
        return Size > sizeBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSmallerThan(long sizeBytes)
    {
        return Size < sizeBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSizeBetween(long minBytes, long maxBytes)
    {
        return Size >= minBytes && Size <= maxBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsNewerThan(DateTime date)
    {
        return ModifiedTicks > date.Ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOlderThan(DateTime date)
    {
        return ModifiedTicks < date.Ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsModifiedBetween(DateTime startDate, DateTime endDate)
    {
        return ModifiedTicks >= startDate.Ticks && ModifiedTicks <= endDate.Ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCreatedBetween(DateTime startDate, DateTime endDate)
    {
        return CreatedTicks >= startDate.Ticks && CreatedTicks <= endDate.Ticks;
    }

    /// <summary>
    /// Two items are equal when their <see cref="FullPath"/>s are, ordinally — the same identity the
    /// full-path intern id used to give.
    /// </summary>
    public override bool Equals(object? obj) => obj is FastFileItem other && this == other;

    public override int GetHashCode() =>
        WithFullPath(0, static (path, _) => string.GetHashCode(path, StringComparison.Ordinal));

    public static bool operator ==(FastFileItem left, FastFileItem right)
    {
        // Same composition from the same interned parts is the same path; ids are ordinal.
        if (left._fullPathId == right._fullPathId
            && (left._fullPathId >= 0
                || (left._directoryPathId == right._directoryPathId && left._nameId == right._nameId)))
        {
            return true;
        }

        if (left.FullPathLength != right.FullPathLength) return false;

        return left.WithFullPath(right, static (path, other) =>
            other.WithFullPath(path, static (otherPath, first) => first.SequenceEqual(otherPath)));
    }

    public static bool operator !=(FastFileItem left, FastFileItem right) => !(left == right);

    // 기존 FileItem과의 호환성을 위한 변환
    public FileItem ToFileItem()
    {
        return new FileItem
        {
            FullPath = FullPath,
            Name = Name,
            DirectoryPath = DirectoryPath,
            Extension = Extension,
            Size = Size,
            CreatedTime = CreatedTime,
            ModifiedTime = ModifiedTime,
            AccessedTime = AccessedTime,
            Attributes = Attributes,
            DriveLetter = DriveLetter,
            FileRecordNumber = FileRecordNumber
        };
    }

    public override string ToString() => Name;
}

/// <summary>
/// FastFileItem 확장 메서드들
/// </summary>
public static class FastFileItemExtensions
{
    // 배치 변환 메서드들
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FastFileItem ToFastFileItem(this FileItem item)
    {
        return new FastFileItem(
            item.FullPath,
            item.Name,
            item.DirectoryPath,
            item.Extension,
            item.Size,
            item.CreatedTime,
            item.ModifiedTime,
            item.AccessedTime,
            item.Attributes,
            item.DriveLetter,
            item.FileRecordNumber
        );
    }

    // 배치 변환
    public static IEnumerable<FastFileItem> ToFastFileItemsBatch(this IEnumerable<FileItem> items)
    {
        return items.Select(item => item.ToFastFileItem());
    }

    // 병렬 배치 변환 - 대용량 데이터용
    public static ParallelQuery<FastFileItem> ToFastFileItemsParallel(this IEnumerable<FileItem> items)
    {
        return items.AsParallel()
                   .WithDegreeOfParallelism(Environment.ProcessorCount)
                   .Select(item => item.ToFastFileItem());
    }

    // SIMD 최적화된 검색 확장 메서드들
    public static IEnumerable<FastFileItem> SearchByName(this IEnumerable<FastFileItem> items, string searchTerm)
    {
        foreach (var item in items)
        {
            if (item.MatchesName(searchTerm))
                yield return item;
        }
    }

    public static IEnumerable<FastFileItem> SearchByPath(this IEnumerable<FastFileItem> items, string searchTerm)
    {
        foreach (var item in items)
        {
            if (item.MatchesPath(searchTerm))
                yield return item;
        }
    }

    public static IEnumerable<FastFileItem> SearchByAny(this IEnumerable<FastFileItem> items, string searchTerm)
    {
        foreach (var item in items)
        {
            if (item.MatchesAny(searchTerm))
                yield return item;
        }
    }

    // 병렬 검색
    public static ParallelQuery<FastFileItem> SearchByNameParallel(this IEnumerable<FastFileItem> items, string searchTerm)
    {
        return items.AsParallel()
                   .WithDegreeOfParallelism(Environment.ProcessorCount)
                   .Where(item => item.MatchesName(searchTerm));
    }

    public static ParallelQuery<FastFileItem> SearchByAnyParallel(this IEnumerable<FastFileItem> items, string searchTerm)
    {
        return items.AsParallel()
                   .WithDegreeOfParallelism(Environment.ProcessorCount)
                   .Where(item => item.MatchesAny(searchTerm));
    }

    // 고성능 필터링
    public static IEnumerable<FastFileItem> FilterBySize(this IEnumerable<FastFileItem> items, long minSize, long maxSize)
    {
        return items.Where(item => item.IsSizeBetween(minSize, maxSize));
    }

    public static IEnumerable<FastFileItem> FilterByModifiedDate(this IEnumerable<FastFileItem> items, DateTime startDate, DateTime endDate)
    {
        return items.Where(item => item.IsModifiedBetween(startDate, endDate));
    }

    public static IEnumerable<FastFileItem> FilterByAttributes(this IEnumerable<FastFileItem> items, FileAttributes attributes, bool mustHaveAll = false)
    {
        return mustHaveAll
            ? items.Where(item => item.HasAllAttributes(attributes))
            : items.Where(item => item.HasAnyAttributes(attributes));
    }

    // 병렬 필터링 - 대용량 데이터용
    public static ParallelQuery<FastFileItem> FilterBySizeParallel(this IEnumerable<FastFileItem> items, long minSize, long maxSize)
    {
        return items.AsParallel()
                   .WithDegreeOfParallelism(Environment.ProcessorCount)
                   .Where(item => item.IsSizeBetween(minSize, maxSize));
    }

    public static ParallelQuery<FastFileItem> FilterByModifiedDateParallel(this IEnumerable<FastFileItem> items, DateTime startDate, DateTime endDate)
    {
        return items.AsParallel()
                   .WithDegreeOfParallelism(Environment.ProcessorCount)
                   .Where(item => item.IsModifiedBetween(startDate, endDate));
    }

    // 통계 메서드들
    public static long GetTotalSize(this IEnumerable<FastFileItem> items)
    {
        return items.Sum(item => item.Size);
    }

    public static int CountDirectories(this IEnumerable<FastFileItem> items)
    {
        return items.Count(item => item.IsDirectory);
    }

    public static int CountFiles(this IEnumerable<FastFileItem> items)
    {
        return items.Count(item => !item.IsDirectory);
    }

    public static FastFileItem? GetNewest(this IEnumerable<FastFileItem> items)
    {
        return items.MaxBy(item => item.ModifiedTicks);
    }

    public static FastFileItem? GetOldest(this IEnumerable<FastFileItem> items)
    {
        return items.MinBy(item => item.ModifiedTicks);
    }

    public static FastFileItem? GetLargest(this IEnumerable<FastFileItem> items)
    {
        return items.MaxBy(item => item.Size);
    }

    public static FastFileItem? GetSmallest(this IEnumerable<FastFileItem> items)
    {
        return items.Where(item => !item.IsDirectory).MinBy(item => item.Size);
    }
}