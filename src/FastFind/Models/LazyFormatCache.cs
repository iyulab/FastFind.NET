using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace FastFind.Models;

/// <summary>
/// Ultra-high performance lazy formatting cache with advanced optimization
/// </summary>
public static class LazyFormatCache
{
    private static readonly ConcurrentDictionary<long, string> _commonSizes = new(Environment.ProcessorCount * 2, 1024);
    private static readonly ConcurrentDictionary<long, string> _largeSizes = new(Environment.ProcessorCount, 512);
    private static readonly ConcurrentDictionary<string, string> _fileTypes = new(Environment.ProcessorCount, 256);
    private static readonly ConcurrentDictionary<long, string> _dateFormats = new(Environment.ProcessorCount, 512);

    private static long _cacheHits = 0;
    private static long _cacheMisses = 0;
    private static long _totalRequests = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetSizeFormatted(long bytes)
    {
        Interlocked.Increment(ref _totalRequests);

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

        var dayTicks = date.Date.Ticks;

        if (_dateFormats.TryGetValue(dayTicks, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        Interlocked.Increment(ref _cacheMisses);
        return _dateFormats.GetOrAdd(dayTicks, _ => date.ToString("yyyy-MM-dd HH:mm"));
    }

    private static readonly string[] SizeUnits = { "bytes", "KB", "MB", "GB", "TB", "PB" };
    private static readonly long[] SizeThresholds = { 1L << 10, 1L << 20, 1L << 30, 1L << 40, 1L << 50 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string FormatFileSizeFast(long bytes)
    {
        if (bytes == 0) return "0 bytes";
        if (bytes < 0) return "Invalid size";

        var unitIndex = 0;
        var value = (double)bytes;

        while (unitIndex < SizeThresholds.Length && bytes >= SizeThresholds[unitIndex])
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {SizeUnits[unitIndex]}"
            : value < 10.0
                ? $"{value:F1} {SizeUnits[unitIndex + 1]}"
                : $"{value:F0} {SizeUnits[unitIndex + 1]}";
    }

    public static void Cleanup()
    {
        var totalMemoryPressure = GC.GetTotalMemory(false);
        var shouldAggressiveClean = totalMemoryPressure > 500_000_000;

        if (_commonSizes.Count > (shouldAggressiveClean ? 5000 : 20000))
        {
            var toRemove = _commonSizes.Take(shouldAggressiveClean ? 2500 : 5000).ToList();
            foreach (var kvp in toRemove)
                _commonSizes.TryRemove(kvp.Key, out _);
        }

        if (_largeSizes.Count > (shouldAggressiveClean ? 2000 : 10000))
        {
            var toRemove = _largeSizes.Take(shouldAggressiveClean ? 1000 : 2500).ToList();
            foreach (var kvp in toRemove)
                _largeSizes.TryRemove(kvp.Key, out _);
        }

        if (_fileTypes.Count > 2000)
        {
            var toRemove = _fileTypes.Take(500).ToList();
            foreach (var kvp in toRemove)
                _fileTypes.TryRemove(kvp.Key, out _);
        }

        if (_dateFormats.Count > (shouldAggressiveClean ? 1000 : 5000))
        {
            var toRemove = _dateFormats.Take(shouldAggressiveClean ? 500 : 1000).ToList();
            foreach (var kvp in toRemove)
                _dateFormats.TryRemove(kvp.Key, out _);
        }

        if (shouldAggressiveClean)
        {
            Interlocked.Exchange(ref _cacheHits, 0);
            Interlocked.Exchange(ref _cacheMisses, 0);
            Interlocked.Exchange(ref _totalRequests, 0);
        }
    }

    public static (long Hits, long Misses, long Total, double HitRatio) GetCacheStats()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var total = Interlocked.Read(ref _totalRequests);
        var hitRatio = total > 0 ? (double)hits / total : 0;

        return (hits, misses, total, hitRatio);
    }

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
        var commonSizesMemory = _commonSizes.Count * (8 + 20);
        var largeSizesMemory = _largeSizes.Count * (8 + 20);
        var fileTypesMemory = _fileTypes.Count * (10 + 15);
        var dateFormatsMemory = _dateFormats.Count * (8 + 16);

        return commonSizesMemory + largeSizesMemory + fileTypesMemory + dateFormatsMemory;
    }
}

/// <summary>
/// LazyFormatCache detailed statistics
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
