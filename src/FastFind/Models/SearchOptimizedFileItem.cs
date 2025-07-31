using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace FastFind.Models;

/// <summary>
/// Search-optimized file item with lazy UI properties for maximum performance
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SearchOptimizedFileItem
{
    // 🔥 검색에 핵심적인 데이터만 즉시 로드 - 특화된 인터닝 사용
    private readonly int _fullPathId;      // 검색 필수
    private readonly int _nameId;          // 검색 필수  
    private readonly int _directoryPathId; // 검색 필수
    private readonly int _extensionId;     // 검색 필수
    
    // ⚡ 원시 데이터만 저장 (표시용 변환은 lazy)
    public readonly long Size;           // 원시 데이터
    public readonly long CreatedTicks;   // 원시 데이터
    public readonly long ModifiedTicks;  // 원시 데이터
    public readonly FileAttributes Attributes;
    public readonly char DriveLetter;
    public readonly ulong FileRecordNumber;
    
    public SearchOptimizedFileItem(string fullPath, string name, string directoryPath, string extension,
                                 long size, DateTime created, DateTime modified, DateTime accessed,
                                 FileAttributes attributes, char driveLetter, ulong? fileRecordNumber = null)
    {
        // 🚀 특화된 인터닝으로 중복 제거율 극대화
        _fullPathId = StringPool.InternPath(fullPath);
        _nameId = StringPool.InternName(name);
        _directoryPathId = StringPool.InternPath(directoryPath);
        _extensionId = StringPool.InternExtension(extension);
        
        Size = size;
        CreatedTicks = created.Ticks;
        ModifiedTicks = modified.Ticks;
        Attributes = attributes;
        DriveLetter = driveLetter;
        FileRecordNumber = fileRecordNumber ?? 0;
    }
    
    // 🚀 즉시 필요한 검색용 속성들 (인라인 최적화)
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
    
    // 💡 Lazy 표시용 속성들 - UI 요청시에만 계산 (더 효율적인 캐싱)
    public string SizeFormatted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => LazyFormatCache.GetSizeFormatted(Size);
    }
    
    public DateTime ModifiedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(ModifiedTicks);
    }
    
    public DateTime CreatedTime
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new DateTime(CreatedTicks);
    }
    
    public string FileType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IsDirectory ? "Folder" : LazyFormatCache.GetFileTypeDescription(Extension);
    }
    
    // 🚀 SIMD 최적화된 검색 메서드들
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesName(string searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(Name.AsSpan(), searchTerm.AsSpan());
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesPath(string searchTerm)
    {
        return SIMDStringMatcher.ContainsVectorized(FullPath.AsSpan(), searchTerm.AsSpan());
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesWildcard(string pattern)
    {
        return SIMDStringMatcher.MatchesWildcard(Name.AsSpan(), pattern.AsSpan());
    }
    
    // 🚀 비트 연산 최적화된 속성 체크들
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAttributes(FileAttributes attrs)
    {
        return (Attributes & attrs) != 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLargerThan(long sizeBytes)
    {
        return Size > sizeBytes;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsNewerThan(DateTime date)
    {
        return ModifiedTicks > date.Ticks;
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
            AccessedTime = new DateTime(ModifiedTicks), // 간소화
            Attributes = Attributes,
            DriveLetter = DriveLetter,
            FileRecordNumber = FileRecordNumber
        };
    }
    
    // 🚀 고성능 비교 연산자들
    public override bool Equals(object? obj)
    {
        return obj is SearchOptimizedFileItem other && _fullPathId == other._fullPathId;
    }
    
    public override int GetHashCode()
    {
        return _fullPathId; // ID 기반 해시는 매우 빠름
    }
    
    public static bool operator ==(SearchOptimizedFileItem left, SearchOptimizedFileItem right)
    {
        return left._fullPathId == right._fullPathId;
    }
    
    public static bool operator !=(SearchOptimizedFileItem left, SearchOptimizedFileItem right)
    {
        return left._fullPathId != right._fullPathId;
    }
}

/// <summary>
/// Ultra-high performance lazy formatting cache with advanced optimization
/// </summary>
public static class LazyFormatCache
{
    // 💡 계층화된 캐시 - 자주 사용되는 크기들 우선 처리
    private static readonly ConcurrentDictionary<long, string> _commonSizes = new(Environment.ProcessorCount * 2, 1024);
    private static readonly ConcurrentDictionary<long, string> _largeSizes = new(Environment.ProcessorCount, 512);
    
    // 💡 파일 타입 캐시 (확장자별) - 매우 효율적
    private static readonly ConcurrentDictionary<string, string> _fileTypes = new(Environment.ProcessorCount, 256);
    
    // 💡 날짜 포맷 캐시 (동일 날짜 파일들이 많음)
    private static readonly ConcurrentDictionary<long, string> _dateFormats = new(Environment.ProcessorCount, 512);
    
    // 🚀 성능 통계 - 원자적 카운터
    private static long _cacheHits = 0;
    private static long _cacheMisses = 0;
    private static long _totalRequests = 0;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetSizeFormatted(long bytes)
    {
        Interlocked.Increment(ref _totalRequests);
        
        // 🚀 계층화된 캐시 - 작은 파일(100MB 이하)은 별도 캐시
        if (bytes < 104_857_600) // 100MB
        {
            if (_commonSizes.TryGetValue(bytes, out var cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }
            
            Interlocked.Increment(ref _cacheMisses);
            return _commonSizes.GetOrAdd(bytes, FormatFileSizeFast);
        }
        else
        {
            if (_largeSizes.TryGetValue(bytes, out var cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }
            
            Interlocked.Increment(ref _cacheMisses);
            return _largeSizes.GetOrAdd(bytes, FormatFileSizeFast);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetFileTypeDescription(string extension)
    {
        Interlocked.Increment(ref _totalRequests);
        
        if (_fileTypes.TryGetValue(extension, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        
        Interlocked.Increment(ref _cacheMisses);
        
        var result = _fileTypes.GetOrAdd(extension.ToLowerInvariant(), ext => ext switch
        {
            ".txt" => "Text Document",
            ".pdf" => "PDF Document", 
            ".doc" or ".docx" => "Word Document",
            ".xls" or ".xlsx" => "Excel Spreadsheet",
            ".ppt" or ".pptx" => "PowerPoint Presentation",
            ".jpg" or ".jpeg" => "JPEG Image",
            ".png" => "PNG Image",
            ".gif" => "GIF Image",
            ".bmp" => "Bitmap Image",
            ".svg" => "SVG Image",
            ".mp4" => "MP4 Video",
            ".avi" => "AVI Video",
            ".mkv" => "MKV Video",
            ".mov" => "MOV Video",
            ".mp3" => "MP3 Audio",
            ".wav" => "WAV Audio",
            ".flac" => "FLAC Audio",
            ".zip" => "ZIP Archive",
            ".rar" => "RAR Archive",
            ".7z" => "7-Zip Archive",
            ".exe" => "Application",
            ".msi" => "Windows Installer",
            ".dll" => "Dynamic Link Library",
            ".sys" => "System File",
            ".cs" => "C# Source",
            ".cpp" => "C++ Source",
            ".js" => "JavaScript",
            ".html" => "HTML Document",
            ".css" => "CSS Stylesheet",
            ".json" => "JSON File",
            ".xml" => "XML Document",
            "" => "File",
            _ => $"{ext.TrimStart('.').ToUpperInvariant()} File"
        });
        
        return result;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetDateFormatted(DateTime date)
    {
        Interlocked.Increment(ref _totalRequests);
        
        // 💡 날짜를 일 단위로 캐싱 (시간 부분 무시)
        var dayTicks = date.Date.Ticks;
        
        if (_dateFormats.TryGetValue(dayTicks, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        
        Interlocked.Increment(ref _cacheMisses);
        return _dateFormats.GetOrAdd(dayTicks, _ => date.ToString("yyyy-MM-dd HH:mm"));
    }
    
    // 🚀 최적화된 크기 포맷팅 (비트 시프트 + 룩업 테이블)
    private static readonly string[] SizeUnits = { "bytes", "KB", "MB", "GB", "TB", "PB" };
    private static readonly long[] SizeThresholds = { 1L << 10, 1L << 20, 1L << 30, 1L << 40, 1L << 50 };
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string FormatFileSizeFast(long bytes)
    {
        if (bytes == 0) return "0 bytes";
        if (bytes < 0) return "Invalid size";
        
        // 🚀 비트 스캔으로 단위 결정 (매우 빠름)
        var unitIndex = 0;
        var value = (double)bytes;
        
        while (unitIndex < SizeThresholds.Length && bytes >= SizeThresholds[unitIndex])
        {
            value /= 1024.0;
            unitIndex++;
        }
        
        // 🚀 정밀도 최적화 - 작은 값은 정수, 큰 값은 소수점 1자리
        return unitIndex == 0 
            ? $"{bytes} {SizeUnits[unitIndex]}"
            : value < 10.0 
                ? $"{value:F1} {SizeUnits[unitIndex + 1]}"
                : $"{value:F0} {SizeUnits[unitIndex + 1]}";
    }
    
    // 🚀 메모리 정리 - 적응형 정리 전략
    public static void Cleanup()
    {
        var totalMemoryPressure = GC.GetTotalMemory(false);
        var shouldAggressiveClean = totalMemoryPressure > 500_000_000; // 500MB 이상
        
        // 크기가 큰 캐시부터 정리
        if (_commonSizes.Count > (shouldAggressiveClean ? 5000 : 20000))
        {
            var toRemove = _commonSizes.Take(shouldAggressiveClean ? 2500 : 5000).ToList();
            foreach (var kvp in toRemove)
            {
                _commonSizes.TryRemove(kvp.Key, out _);
            }
        }
        
        if (_largeSizes.Count > (shouldAggressiveClean ? 2000 : 10000))
        {
            var toRemove = _largeSizes.Take(shouldAggressiveClean ? 1000 : 2500).ToList();
            foreach (var kvp in toRemove)
            {
                _largeSizes.TryRemove(kvp.Key, out _);
            }
        }
        
        if (_fileTypes.Count > 2000) 
        {
            // 파일 타입은 상대적으로 적으므로 보수적으로 정리
            var toRemove = _fileTypes.Take(500).ToList();
            foreach (var kvp in toRemove)
            {
                _fileTypes.TryRemove(kvp.Key, out _);
            }
        }
        
        if (_dateFormats.Count > (shouldAggressiveClean ? 1000 : 5000))
        {
            var toRemove = _dateFormats.Take(shouldAggressiveClean ? 500 : 1000).ToList();
            foreach (var kvp in toRemove)
            {
                _dateFormats.TryRemove(kvp.Key, out _);
            }
        }
        
        // 통계 초기화 (선택적)
        if (shouldAggressiveClean)
        {
            Interlocked.Exchange(ref _cacheHits, 0);
            Interlocked.Exchange(ref _cacheMisses, 0);
            Interlocked.Exchange(ref _totalRequests, 0);
        }
    }
    
    // 통계 정보
    public static (long Hits, long Misses, long Total, double HitRatio) GetCacheStats()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var total = Interlocked.Read(ref _totalRequests);
        var hitRatio = total > 0 ? (double)hits / total : 0;
        
        return (hits, misses, total, hitRatio);
    }
    
    // 상세 통계
    public static LazyFormatCacheStats GetDetailedStats()
    {
        var (hits, misses, total, hitRatio) = GetCacheStats();
        
        return new LazyFormatCacheStats(
            hits, misses, total, hitRatio,
            _commonSizes.Count, _largeSizes.Count,
            _fileTypes.Count, _dateFormats.Count,
            EstimateMemoryUsage()
        );
    }
    
    private static long EstimateMemoryUsage()
    {
        // 대략적인 메모리 사용량 계산
        var commonSizesMemory = _commonSizes.Count * (8 + 20); // long key + avg string
        var largeSizesMemory = _largeSizes.Count * (8 + 20);
        var fileTypesMemory = _fileTypes.Count * (10 + 15); // avg key + avg value
        var dateFormatsMemory = _dateFormats.Count * (8 + 16); // long key + date string
        
        return commonSizesMemory + largeSizesMemory + fileTypesMemory + dateFormatsMemory;
    }
}

/// <summary>
/// LazyFormatCache 상세 통계
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct LazyFormatCacheStats
{
    public readonly long CacheHits;
    public readonly long CacheMisses;
    public readonly long TotalRequests;
    public readonly double HitRatio;
    public readonly int CommonSizesCount;
    public readonly int LargeSizesCount;
    public readonly int FileTypesCount;
    public readonly int DateFormatsCount;
    public readonly long EstimatedMemoryUsage;
    
    public LazyFormatCacheStats(long cacheHits, long cacheMisses, long totalRequests, double hitRatio,
                               int commonSizesCount, int largeSizesCount, int fileTypesCount, 
                               int dateFormatsCount, long estimatedMemoryUsage)
    {
        CacheHits = cacheHits;
        CacheMisses = cacheMisses;
        TotalRequests = totalRequests;
        HitRatio = hitRatio;
        CommonSizesCount = commonSizesCount;
        LargeSizesCount = largeSizesCount;
        FileTypesCount = fileTypesCount;
        DateFormatsCount = dateFormatsCount;
        EstimatedMemoryUsage = estimatedMemoryUsage;
    }
    
    public double HitRatioPercentage => HitRatio * 100.0;
    public double MemoryUsageMB => EstimatedMemoryUsage / (1024.0 * 1024.0);
}

/// <summary>
/// Compatibility extensions for SearchOptimizedFileItem
/// </summary>
public static class SearchOptimizedFileItemExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SearchOptimizedFileItem ToSearchOptimized(this FileItem item)
    {
        return new SearchOptimizedFileItem(
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
    
    // 🚀 배치 변환 - 더 효율적
    public static IEnumerable<SearchOptimizedFileItem> ToSearchOptimizedBatch(this IEnumerable<FileItem> items)
    {
        return items.Select(item => item.ToSearchOptimized());
    }
    
    // 🚀 병렬 배치 변환 - 대용량 데이터용
    public static ParallelQuery<SearchOptimizedFileItem> ToSearchOptimizedParallel(this IEnumerable<FileItem> items)
    {
        return items.AsParallel()
                   .WithDegreeOfParallelism(Environment.ProcessorCount)
                   .Select(item => item.ToSearchOptimized());
    }
}