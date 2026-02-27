# FastFind.NET API Reference

## Core Interfaces

### ISearchEngine

```csharp
public interface ISearchEngine : IDisposable
{
    // Search
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default);
    Task<SearchResult> SearchAsync(string searchText, CancellationToken ct = default);
    IAsyncEnumerable<SearchResult> SearchRealTimeAsync(SearchQuery query, CancellationToken ct = default);

    // Index management
    Task StartIndexingAsync(IndexingOptions options, CancellationToken ct = default);
    Task StopIndexingAsync(CancellationToken ct = default);
    Task RefreshIndexAsync(IEnumerable<string>? locations = null, CancellationToken ct = default);

    // State
    bool IsIndexing { get; }
    bool IsMonitoring { get; }
    long TotalIndexedFiles { get; }

    // Events
    event EventHandler<IndexingProgressEventArgs>? IndexingProgressChanged;
    event EventHandler<FileChangeEventArgs>? FileChanged;
    event EventHandler<SearchProgressEventArgs>? SearchProgressChanged;
}
```

### FastFileItem

Ultra-optimized 61-byte struct with SIMD acceleration and string interning.

```csharp
public readonly struct FastFileItem
{
    // String-interned properties (resolved via StringPool)
    public string FullPath { get; }
    public string Name { get; }
    public string DirectoryPath { get; }
    public string Extension { get; }

    // SIMD-accelerated matching
    public bool MatchesName(ReadOnlySpan<char> searchTerm);
    public bool MatchesPath(ReadOnlySpan<char> searchTerm);
    public bool MatchesWildcard(ReadOnlySpan<char> pattern);

    // Metadata
    public long Size { get; }
    public string SizeFormatted { get; }      // Lazy-cached: "1.5 MB"
    public string FileType { get; }           // Lazy-cached: "C# Source"
    public DateTime CreatedTime { get; }
    public DateTime ModifiedTime { get; }
    public DateTime AccessedTime { get; }
    public FileAttributes Attributes { get; }
    public bool IsDirectory { get; }
}
```

### SearchQuery

```csharp
public class SearchQuery
{
    // Path
    public string? BasePath { get; set; }
    public string SearchText { get; set; } = "";
    public bool IncludeSubdirectories { get; set; } = true;
    public bool SearchFileNameOnly { get; set; } = false;

    // Behavior
    public bool UseRegex { get; set; } = false;
    public bool CaseSensitive { get; set; } = false;

    // Filters
    public string? ExtensionFilter { get; set; }
    public bool IncludeFiles { get; set; } = true;
    public bool IncludeDirectories { get; set; } = true;
    public bool IncludeHidden { get; set; } = false;
    public bool IncludeSystem { get; set; } = false;
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateTime? MinCreatedDate { get; set; }
    public DateTime? MaxCreatedDate { get; set; }
    public DateTime? MinModifiedDate { get; set; }
    public DateTime? MaxModifiedDate { get; set; }

    // Result control
    public int? MaxResults { get; set; }
    public IList<string> SearchLocations { get; set; }
    public IList<string> ExcludedPaths { get; set; }

    // Utility
    public (bool IsValid, string? ErrorMessage) Validate();
    public SearchQuery Clone();
}
```

## Platform APIs

### Windows — MFT Direct Access

```csharp
// MftReader: NTFS MFT enumeration (requires admin)
public class MftReader : IDisposable
{
    public static bool IsAvailable();
    public static char[] GetNtfsDrives();
    public IAsyncEnumerable<MftFileRecord> EnumerateFilesAsync(char driveLetter, CancellationToken ct = default);
}

// MftSqlitePipeline: MFT → SQLite data flow
public class MftSqlitePipeline : IDisposable
{
    public Task<int> IndexAllDrivesAsync(IIndexPersistence persistence, IProgress<IndexingProgress>? progress = null, CancellationToken ct = default);
    public Task<int> IndexDrivesAsync(char[] driveLetters, IIndexPersistence persistence, IProgress<IndexingProgress>? progress = null, CancellationToken ct = default);
}

// UsnSqliteSyncService: Real-time USN Journal sync
public class UsnSqliteSyncService : IAsyncDisposable
{
    public Task StartAsync(CancellationToken ct = default);
    public Task StartAsync(char[] driveLetters, CancellationToken ct = default);
    public Task StopAsync();
    public bool IsRunning { get; }
    public SyncStatistics Statistics { get; }
}
```

### Linux — Channel-based BFS

```csharp
// LinuxFileSystemProvider: parallel file enumeration
public class LinuxFileSystemProvider : IFileSystemProvider
{
    public IAsyncEnumerable<FileItem> EnumerateFilesAsync(IndexingOptions options, CancellationToken ct = default);
    public Task<IReadOnlyList<DriveInfo>> GetAvailableLocationsAsync(CancellationToken ct = default);
    public IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(MonitoringOptions options, CancellationToken ct = default);
    public Task<string> GetFileSystemTypeAsync(string path, CancellationToken ct = default);
    public bool IsAvailable { get; }
}
```

### SQLite Persistence

```csharp
public class SqlitePersistence : IIndexPersistence
{
    // Factory
    public static SqlitePersistence Create(string databasePath, ILogger? logger = null);
    public static SqlitePersistence CreateHighPerformance(string databasePath, ILogger? logger = null);

    // CRUD
    public Task InitializeAsync(CancellationToken ct = default);
    public Task AddAsync(FastFileItem item, CancellationToken ct = default);
    public Task<int> AddBulkOptimizedAsync(IList<FastFileItem> items, CancellationToken ct = default);
    public Task<int> AddFromStreamAsync(IAsyncEnumerable<FastFileItem> items, int bufferSize = 5000, IProgress<int>? progress = null, CancellationToken ct = default);

    // Query (FTS5)
    public IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, CancellationToken ct = default);
    public IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken ct = default);
    public IAsyncEnumerable<FastFileItem> GetByExtensionAsync(string extension, CancellationToken ct = default);

    // Maintenance
    public Task OptimizeAsync(CancellationToken ct = default);
    public Task VacuumAsync(CancellationToken ct = default);
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default);
}
```
