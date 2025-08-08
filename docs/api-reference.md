# API Reference

Complete API documentation for FastFind.NET Core and Windows implementations.

## 📦 Package Structure

### FastFind.Core
Core interfaces, models, and abstractions used across all platforms.

### FastFind.Windows
Windows-specific implementation with NTFS optimizations.

### FastFind.Unix (🚧 Roadmap)
Unix/Linux implementation (planned for future release).

## 🏗️ Core Interfaces

### ISearchEngine

Primary interface for file search operations.

```csharp
public interface ISearchEngine : IDisposable
{
    // Search Operations
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<SearchResult> SearchAsync(string searchText, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SearchResult> SearchRealTimeAsync(SearchQuery query, CancellationToken cancellationToken = default);
    
    // Index Management
    Task StartIndexingAsync(IndexingOptions options, CancellationToken cancellationToken = default);
    Task StopIndexingAsync(CancellationToken cancellationToken = default);
    Task RefreshIndexAsync(IEnumerable<string>? locations = null, CancellationToken cancellationToken = default);
    Task OptimizeIndexAsync(CancellationToken cancellationToken = default);
    
    // Persistence
    Task SaveIndexAsync(string? filePath = null, CancellationToken cancellationToken = default);
    Task LoadIndexAsync(string? filePath = null, CancellationToken cancellationToken = default);
    
    // Statistics
    Task<SearchStatistics> GetSearchStatisticsAsync(CancellationToken cancellationToken = default);
    Task<IndexingStatistics> GetIndexingStatisticsAsync(CancellationToken cancellationToken = default);
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
    
    // Properties
    bool IsIndexing { get; }
    bool IsMonitoring { get; }
    long TotalIndexedFiles { get; }
    
    // Events
    event EventHandler<IndexingProgressEventArgs>? IndexingProgressChanged;
    event EventHandler<FileChangeEventArgs>? FileChanged;
    event EventHandler<SearchProgressEventArgs>? SearchProgressChanged;
}
```

### IFileSystemProvider

Platform-specific file system access interface.

```csharp
public interface IFileSystemProvider : IDisposable
{
    PlatformType SupportedPlatform { get; }
    bool IsAvailable { get; }
    
    // File Enumeration
    IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations, 
        IndexingOptions options, 
        CancellationToken cancellationToken = default);
    
    // File Information
    Task<FileItem?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
    Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default);
    
    // System Information
    Task<IEnumerable<string>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default);
    Task<PerformanceInfo> GetPerformanceInfo();
    
    // Monitoring
    IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        CancellationToken cancellationToken = default);
}
```

### ISearchIndex

Search index management interface.

```csharp
public interface ISearchIndex : IDisposable
{
    // Index Operations
    Task AddFilesAsync(IAsyncEnumerable<FileItem> files, CancellationToken cancellationToken = default);
    Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task UpdateFileAsync(FileItem file, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task OptimizeAsync(CancellationToken cancellationToken = default);
    
    // Search Operations
    Task<IEnumerable<FileItem>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    
    // Statistics
    long Count { get; }
    Task<IndexStatistics> GetStatisticsAsync();
    
    // Persistence
    Task SaveToFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default);
}
```

## 📋 Core Models

### FileItem

Standard file information model.

```csharp
public class FileItem
{
    public string FullPath { get; set; }
    public string Name { get; set; }
    public string Directory { get; set; }
    public string Extension { get; set; }
    public long Size { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime ModifiedTime { get; set; }
    public DateTime AccessedTime { get; set; }
    public FileAttributes Attributes { get; set; }
    
    // Computed properties
    public string SizeFormatted { get; }
    public string FileType { get; }
    public bool IsHidden { get; }
    public bool IsDirectory { get; }
}
```

### FastFileItem

Memory-optimized struct version using string interning.

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct FastFileItem : IEquatable<FastFileItem>
{
    public readonly int FullPathId;
    public readonly int NameId;
    public readonly int DirectoryId;
    public readonly int ExtensionId;
    public readonly long Size;
    public readonly DateTime CreatedTime;
    public readonly DateTime ModifiedTime;
    public readonly DateTime AccessedTime;
    public readonly FileAttributes Attributes;
    public readonly char DriveLetter;
    
    // Performance methods
    public bool MatchesName(ReadOnlySpan<char> pattern);
    public bool MatchesPath(ReadOnlySpan<char> pattern);
    public bool MatchesWildcard(ReadOnlySpan<char> pattern);
    
    // Conversion methods
    public FileItem ToFileItem();
    public SearchOptimizedFileItem ToSearchOptimized();
}
```

### SearchOptimizedFileItem

UI-optimized model with lazy loading.

```csharp
public class SearchOptimizedFileItem
{
    // Core properties (always loaded)
    public string FullPath { get; }
    public string Name { get; }
    public long Size { get; }
    public DateTime ModifiedTime { get; }
    
    // Lazy properties (loaded on first access)
    public string SizeFormatted { get; }
    public string FileType { get; }
    public string Directory { get; }
    public string Extension { get; }
    public FileAttributes Attributes { get; }
    
    // UI-specific properties
    public string DisplayName { get; }
    public string ToolTip { get; }
    public int IconIndex { get; }
}
```

### SearchQuery

Comprehensive search parameters.

```csharp
public class SearchQuery
{
    // Basic search
    public string SearchText { get; set; } = string.Empty;
    public bool UseRegex { get; set; } = false;
    public bool CaseSensitive { get; set; } = false;
    public bool SearchFileNameOnly { get; set; } = true;
    
    // Type filters
    public bool IncludeFiles { get; set; } = true;
    public bool IncludeDirectories { get; set; } = false;
    public string? ExtensionFilter { get; set; }
    
    // Size filters
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    
    // Date filters
    public DateTime? MinCreatedDate { get; set; }
    public DateTime? MaxCreatedDate { get; set; }
    public DateTime? MinModifiedDate { get; set; }
    public DateTime? MaxModifiedDate { get; set; }
    
    // Result limits
    public int MaxResults { get; set; } = 10000;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    
    // Advanced options
    public bool EnableHighlighting { get; set; } = false;
    public SearchSortOrder SortOrder { get; set; } = SearchSortOrder.Relevance;
    public bool ExactMatch { get; set; } = false;
}
```

### SearchResult

Search operation results with metadata.

```csharp
public class SearchResult
{
    public IReadOnlyList<FileItem> Files { get; }
    public long TotalMatches { get; }
    public TimeSpan SearchTime { get; }
    public SearchPhase Phase { get; }
    public bool IsCompleted { get; }
    public bool WasCancelled { get; }
    public string? ErrorMessage { get; }
    
    // Performance metrics
    public long IndexHits { get; }
    public long CacheHits { get; }
    public double EfficiencyScore { get; }
}
```

## ⚙️ Configuration Models

### IndexingOptions

Configuration for indexing operations.

```csharp
public class IndexingOptions
{
    // Location settings
    public IList<char> DriveLetters { get; set; } = ['C'];
    public IList<string> MountPoints { get; set; } = ["/"];
    public IList<string> SpecificDirectories { get; set; } = [];
    
    // Filtering
    public IList<string> ExcludedPaths { get; set; } = ["temp", "cache"];
    public IList<string> ExcludedExtensions { get; set; } = [".tmp"];
    public bool IncludeHidden { get; set; } = false;
    public bool IncludeSystem { get; set; } = false;
    
    // Performance
    public long MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB
    public int ParallelThreads { get; set; } = Environment.ProcessorCount;
    public int BatchSize { get; set; } = 1000;
    public TimeSpan IndexingTimeout { get; set; } = TimeSpan.FromHours(2);
    
    // Advanced
    public bool EnableRealTimeMonitoring { get; set; } = true;
    public bool OptimizeForSSD { get; set; } = true;
    public bool UseMemoryMappedFiles { get; set; } = false;
}
```

## 📊 Statistics Models

### SearchStatistics

Performance metrics for search operations.

```csharp
public class SearchStatistics
{
    public long TotalSearches { get; }
    public TimeSpan AverageSearchTime { get; }
    public TimeSpan TotalSearchTime { get; }
    public double CacheHitRate { get; }
    public long IndexHits { get; }
    public long TotalResults { get; }
    public DateTime LastSearchTime { get; }
    
    // Performance breakdown
    public TimeSpan IndexSearchTime { get; }
    public TimeSpan FilteringTime { get; }
    public TimeSpan SortingTime { get; }
}
```

### IndexingStatistics

Metrics for indexing operations.

```csharp
public class IndexingStatistics
{
    public long TotalFiles { get; }
    public long ProcessedFiles { get; }
    public TimeSpan IndexingTime { get; }
    public long FilesPerSecond { get; }
    public long TotalBytes { get; }
    public DateTime LastIndexTime { get; }
    
    // Memory usage
    public long MemoryUsageBytes { get; }
    public long StringPoolSize { get; }
    public double CompressionRatio { get; }
}
```

## 🎯 Enumerations

### PlatformType

```csharp
public enum PlatformType
{
    Windows,
    Unix,
    Custom
}
```

### SearchPhase

```csharp
public enum SearchPhase
{
    Initializing,
    SearchingIndex,
    FilteringResults,
    SortingResults,
    Completed,
    Failed,
    Cancelled
}
```

### FileChangeType

```csharp
public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed,
    Moved
}
```

## 🚀 Factory Methods

### FastFinder

Main factory class for creating search engines.

```csharp
public static class FastFinder
{
    // Primary factory method
    public static ISearchEngine CreateSearchEngine(ILogger? logger = null);
    
    // Platform-specific creation
    public static ISearchEngine CreateWindowsSearchEngine(ILogger? logger = null);
    public static ISearchEngine CreateUnixSearchEngine(ILogger? logger = null); // 🚧 Roadmap
    
    // System validation
    public static SystemValidationResult ValidateSystem();
    
    // Custom providers
    public static void RegisterSearchEngineFactory(PlatformType platform, 
        Func<ILogger?, ISearchEngine> factory);
}
```

## 🔧 Utility Classes

### SIMDStringMatcher

Hardware-accelerated string operations.

```csharp
public static class SIMDStringMatcher
{
    public static bool ContainsVectorized(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern);
    public static bool MatchesWildcard(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern);
    public static bool ContainsIgnoreCase(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern);
    public static int IndexOfVectorized(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern);
}
```

### StringPool

Memory-efficient string interning.

```csharp
public static class StringPool
{
    public static int InternPath(string path);
    public static int InternExtension(string extension);
    public static int InternName(string name);
    public static string GetString(int id);
    
    public static StringPoolStats GetStats();
    public static void Cleanup();
    public static void CompactMemory();
}
```

## 📋 Event Arguments

### IndexingProgressEventArgs

```csharp
public class IndexingProgressEventArgs : EventArgs
{
    public string Location { get; }
    public long ProcessedFiles { get; }
    public long TotalFiles { get; }
    public double ProgressPercentage { get; }
    public long FilesPerSecond { get; }
    public TimeSpan ElapsedTime { get; }
    public TimeSpan EstimatedTimeRemaining { get; }
}
```

### FileChangeEventArgs

```csharp
public class FileChangeEventArgs : EventArgs
{
    public FileChangeType ChangeType { get; }
    public string FullPath { get; }
    public string? OldPath { get; } // For renamed/moved files
    public DateTime ChangeTime { get; }
}
```

## 🌐 Platform-Specific APIs

### Windows-Specific (FastFind.Windows)

Additional Windows-specific functionality:

```csharp
namespace FastFind.Windows;

public class WindowsSearchEngine : ISearchEngine
{
    // Windows-specific features
    public Task<IEnumerable<string>> GetJunctionLinksAsync(string directory);
    public Task<VolumeInfo> GetVolumeInfoAsync(char driveLetter);
    public Task EnableVSSIndexingAsync(bool enable);
}

public class WindowsFileSystemProvider : IFileSystemProvider
{
    // NTFS-specific optimizations
    public Task<MFTRecord[]> ReadMFTAsync(char driveLetter);
    public Task<bool> SupportsHardLinksAsync(string path);
}
```