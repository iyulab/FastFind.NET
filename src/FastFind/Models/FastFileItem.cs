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
    // 인터닝된 문자열 ID로 메모리 절약 - 특화된 인터닝 사용
    private readonly int _fullPathId;
    private readonly int _nameId;
    private readonly int _directoryPathId;
    private readonly int _extensionId;

    // Public ID 접근자들
    public int FullPathId => _fullPathId;
    public int NameId => _nameId;
    public int DirectoryId => _directoryPathId;
    public int ExtensionId => _extensionId;

    // 원시 타입으로 최대 성능
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
        // 특화된 인터닝으로 중복 제거율 극대화
        _fullPathId = StringPool.InternPath(fullPath);
        _nameId = StringPool.InternName(name);
        _directoryPathId = StringPool.InternPath(directoryPath);
        _extensionId = StringPool.InternExtension(extension);

        Size = size;
        CreatedTicks = created.Ticks;
        ModifiedTicks = modified.Ticks;
        AccessedTicks = accessed.Ticks;
        Attributes = attributes;
        DriveLetter = driveLetter;
        FileRecordNumber = fileRecordNumber ?? 0;
    }

    // 고성능 속성 접근자 - 인라인 최적화
    public string FullPath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StringPool.Get(_fullPathId);
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

    public DateTime CreatedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(CreatedTicks);
    }

    public DateTime ModifiedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(ModifiedTicks);
    }

    public DateTime AccessedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(AccessedTicks);
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesPath(string searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(FullPath.AsSpan(), searchTerm.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesPath(ReadOnlySpan<char> searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(FullPath.AsSpan(), searchTerm);
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

    // 고성능 비교 연산자들
    public override bool Equals(object? obj)
    {
        return obj is FastFileItem other && _fullPathId == other._fullPathId;
    }

    public override int GetHashCode()
    {
        return _fullPathId; // ID 기반 해시는 매우 빠름
    }

    public static bool operator ==(FastFileItem left, FastFileItem right)
    {
        return left._fullPathId == right._fullPathId;
    }

    public static bool operator !=(FastFileItem left, FastFileItem right)
    {
        return left._fullPathId != right._fullPathId;
    }

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

    // SearchOptimizedFileItem으로 변환
    public SearchOptimizedFileItem ToSearchOptimized()
    {
        return new SearchOptimizedFileItem(
            FullPath, Name, DirectoryPath, Extension,
            Size, CreatedTime, ModifiedTime, AccessedTime,
            Attributes, DriveLetter, FileRecordNumber
        );
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