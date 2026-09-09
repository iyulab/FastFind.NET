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

    // Index persistence — throws InvalidOperationException when the engine has no store.
    Task<int> SaveIndexAsync(CancellationToken ct = default);
    Task<int> LoadIndexAsync(CancellationToken ct = default);

    // State
    bool IsIndexing { get; }
    bool IsMonitoring { get; }
    long TotalIndexedFiles { get; }

    // The index this engine searches; null for an engine that keeps its index internally.
    ISearchIndex? Index { get; }

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

### macOS — Channel-based BFS

```csharp
// MacOSFileSystemProvider: parallel file enumeration with DriveInfo + /Volumes mount detection
public class MacOSFileSystemProvider : IFileSystemProvider
{
    public IAsyncEnumerable<FileItem> EnumerateFilesAsync(IndexingOptions options, CancellationToken ct = default);
    public Task<IReadOnlyList<DriveInfo>> GetAvailableLocationsAsync(CancellationToken ct = default);
    public IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(MonitoringOptions options, CancellationToken ct = default);
    public Task<string> GetFileSystemTypeAsync(string path, CancellationToken ct = default);
    public bool IsAvailable { get; }
}
```

### Composing an engine

```csharp
// The index lives in the store; the engine holds no copy.
using var engine = FastFinder.CreateSearchEngine(store);

// Or spell out every input.
using var engine = FastFinder.CreateSearchEngine(new SearchEngineOptions
{
    LoggerFactory = loggerFactory,
    Persistence = store,
    PersistenceMode = PersistenceMode.QueryFromStore, // or MirrorInMemory
    Index = null,                                     // supply your own ISearchIndex instead
    DisposeSuppliedComponents = false                 // the caller keeps ownership by default
});
```

`PersistenceMode.QueryFromStore` answers queries from the store, so the engine's memory does not grow
with the corpus. `MirrorInMemory` keeps the in-memory index and writes through to the store, costing
the memory of both.

Every platform can be composed with a store. The Linux and macOS engines support
`QueryFromStore`; `MirrorInMemory` is rejected there with `NotSupportedException`, because it needs a
shared in-memory `ISearchIndex` to mirror into and those engines keep their default in-memory index
in a plain dictionary. Failing loudly is deliberate — accepting the mode and not mirroring would
leave a caller believing their index was durable.

A store-backed engine reports `IndexingStatistics.TotalSize` as 0: summing sizes means reading every
row, which is what a store-backed index exists to avoid.

### Query semantics

Every index and persistence provider evaluates matches through `SearchQueryEvaluator`, so a query
returns the same set whichever backend answers it. A backend may narrow candidates first — by SQL
predicate, by an extension index, by a path trie — but only in ways that cannot exclude a match the
evaluator accepts.

`SearchQuery.RequiredAttributes` and `ExcludedAttributes` are currently not honoured by any backend.

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

    // Query
    public IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, CancellationToken ct = default);
    public IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken ct = default);
    public IAsyncEnumerable<FastFileItem> GetByExtensionAsync(string extension, CancellationToken ct = default);

    // Maintenance
    public Task OptimizeAsync(CancellationToken ct = default);
    public Task VacuumAsync(CancellationToken ct = default);
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default);
}
```
