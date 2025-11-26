# FastFind.NET API Reference

## Enhanced Search Query API ⚡

### SearchQuery Class
Comprehensive search configuration with enhanced path-based options.

```csharp
public class SearchQuery
{
    // 🎯 Enhanced Path-Based Search Options
    public string? BasePath { get; set; }               // Single base path for search
    public string SearchText { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; } = true;
    public bool SearchFileNameOnly { get; set; } = false;  // Default: search full paths

    // 🔍 Search Behavior
    public bool UseRegex { get; set; } = false;
    public bool CaseSensitive { get; set; } = false;

    // 📂 File Filters
    public string? ExtensionFilter { get; set; }
    public bool IncludeFiles { get; set; } = true;
    public bool IncludeDirectories { get; set; } = true;
    public bool IncludeHidden { get; set; } = false;
    public bool IncludeSystem { get; set; } = false;

    // 📏 Size and Date Filters
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateTime? MinCreatedDate { get; set; }
    public DateTime? MaxCreatedDate { get; set; }
    public DateTime? MinModifiedDate { get; set; }
    public DateTime? MaxModifiedDate { get; set; }

    // 📊 Result Control
    public int? MaxResults { get; set; }
    public IList<string> SearchLocations { get; set; }   // Used if BasePath not set
    public IList<string> ExcludedPaths { get; set; }

    // 🔧 Utility Methods
    public (bool IsValid, string? ErrorMessage) Validate();
    public SearchQuery Clone();
    public Regex? GetCompiledRegex();
    public Regex? GetWildcardRegex();
}
```

## Core Interfaces

### ISearchEngine
Primary interface for file search operations with enhanced async support.

```csharp
public interface ISearchEngine : IDisposable
{
    // Core search operations
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<SearchResult> SearchAsync(string searchText, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SearchResult> SearchRealTimeAsync(SearchQuery query, CancellationToken cancellationToken = default);

    // Index management
    Task StartIndexingAsync(IndexingOptions options, CancellationToken cancellationToken = default);
    Task StopIndexingAsync(CancellationToken cancellationToken = default);
    Task RefreshIndexAsync(IEnumerable<string>? locations = null, CancellationToken cancellationToken = default);

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

### FastFileItem
Ultra-optimized 61-byte struct with SIMD acceleration.

```csharp
public readonly struct FastFileItem
{
    // String-interned properties
    public string FullPath => StringPool.GetString(FullPathId);
    public string Name => StringPool.GetString(NameId);
    public string DirectoryPath => StringPool.GetString(DirectoryId);
    public string Extension => StringPool.GetString(ExtensionId);

    // High-performance SIMD methods
    public bool MatchesName(ReadOnlySpan<char> searchTerm);
    public bool MatchesPath(ReadOnlySpan<char> searchTerm);
    public bool MatchesWildcard(ReadOnlySpan<char> pattern);

    // File metadata
    public long Size { get; }
    public DateTime CreatedTime { get; }
    public DateTime ModifiedTime { get; }
    public DateTime AccessedTime { get; }
    public FileAttributes Attributes { get; }
}
```

## Enhanced Search Examples

### 1. Base Path Search
```csharp
// Search from specific directory with subdirectories
var query = new SearchQuery
{
    BasePath = @"D:\Projects",          // 기준경로: Start from this path
    SearchText = "Controller",          // search-text: Pattern to find
    IncludeSubdirectories = true,       // subdirectory: Include subdirs
    SearchFileNameOnly = false,         // Search in full paths
    ExtensionFilter = ".cs"
};

WindowsRegistration.EnsureRegistered();
using var searchEngine = FastFinder.CreateWindowsSearchEngine();

var results = await searchEngine.SearchAsync(query);
await foreach (var file in results.Files)
{
    Console.WriteLine($"📄 {file.FullPath}");
}
```

### 2. Filename vs Full Path Search
```csharp
// Compare filename-only vs full-path search
var filenameQuery = new SearchQuery
{
    SearchText = "config",
    SearchFileNameOnly = true,          // Only search filenames
    BasePath = @"C:\Program Files"
};

var fullPathQuery = new SearchQuery
{
    SearchText = "config",
    SearchFileNameOnly = false,         // Search full paths + filenames
    BasePath = @"C:\Program Files"
};

var filenameResults = await searchEngine.SearchAsync(filenameQuery);
var fullPathResults = await searchEngine.SearchAsync(fullPathQuery);

Console.WriteLine($"Filename only: {filenameResults.TotalMatches} matches");
Console.WriteLine($"Full path: {fullPathResults.TotalMatches} matches");
```

### 3. Subdirectory Control
```csharp
// Search with and without subdirectories
var directOnlyQuery = new SearchQuery
{
    BasePath = @"C:\Users\Downloads",
    SearchText = ".exe",
    IncludeSubdirectories = false,      // Only direct files
    SearchFileNameOnly = false
};

var recursiveQuery = new SearchQuery
{
    BasePath = @"C:\Users\Downloads",
    SearchText = ".exe",
    IncludeSubdirectories = true,       // Include all subdirectories
    SearchFileNameOnly = false
};
```

## SQLite Persistence API

### SqlitePersistence Class
High-performance SQLite persistence with FTS5 full-text search.

```csharp
public class SqlitePersistence : IIndexPersistence
{
    // Factory methods
    public static SqlitePersistence Create(string databasePath, ILogger? logger = null);
    public static SqlitePersistence CreateHighPerformance(string databasePath, ILogger? logger = null);

    // Initialization
    public Task InitializeAsync(CancellationToken cancellationToken = default);

    // Add operations
    public Task AddAsync(FastFileItem item, CancellationToken cancellationToken = default);
    public Task<int> AddBatchAsync(IEnumerable<FastFileItem> items, CancellationToken cancellationToken = default);
    public Task<int> AddBulkOptimizedAsync(IList<FastFileItem> items, CancellationToken cancellationToken = default);
    public Task<int> AddFromStreamAsync(IAsyncEnumerable<FastFileItem> items, int bufferSize = 5000, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    // Query operations
    public IAsyncEnumerable<FastFileItem> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    public IAsyncEnumerable<FastFileItem> GetByDirectoryAsync(string directoryPath, bool recursive = false, CancellationToken cancellationToken = default);
    public IAsyncEnumerable<FastFileItem> GetByExtensionAsync(string extension, CancellationToken cancellationToken = default);
    public Task<FastFileItem?> GetAsync(string fullPath, CancellationToken cancellationToken = default);

    // Maintenance
    public Task OptimizeAsync(CancellationToken cancellationToken = default);
    public Task VacuumAsync(CancellationToken cancellationToken = default);
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    // Properties
    public long Count { get; }
    public bool IsReady { get; }
    public string StoragePath { get; }
}
```

### Usage Example
```csharp
using FastFind.SQLite;

// Create high-performance SQLite persistence
await using var persistence = SqlitePersistence.CreateHighPerformance("index.db");
await persistence.InitializeAsync();

// Bulk insert files
var items = GetFileItems();
var inserted = await persistence.AddBulkOptimizedAsync(items);

// FTS5 search
var results = await persistence.SearchAsync(new SearchQuery
{
    SearchText = "document",
    ExtensionFilter = ".pdf",
    MaxResults = 100
}).ToListAsync();

// Get statistics
var stats = await persistence.GetStatisticsAsync();
Console.WriteLine($"Total: {stats.TotalItems}, Files: {stats.TotalFiles}, Dirs: {stats.TotalDirectories}");
```

## MFT Direct Access API

### MftSqlitePipeline Class
High-throughput pipeline connecting MFT enumeration to SQLite persistence.

```csharp
public class MftSqlitePipeline : IDisposable
{
    // Main indexing methods
    public Task<int> IndexAllDrivesAsync(
        IIndexPersistence persistence,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    public Task<int> IndexDrivesAsync(
        char[] driveLetters,
        IIndexPersistence persistence,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    // Statistics
    public PipelineStatistics Statistics { get; }
}
```

### MftReader Class
Direct NTFS MFT enumeration for ultra-fast file discovery.

```csharp
public class MftReader : IDisposable
{
    // Static utility methods
    public static bool IsAvailable();
    public static char[] GetNtfsDrives();

    // Enumeration
    public IAsyncEnumerable<MftFileRecord> EnumerateFilesAsync(
        char driveLetter,
        CancellationToken cancellationToken = default);
}
```

### Usage Example
```csharp
using FastFind.Windows.Mft;
using FastFind.SQLite;

// Check MFT availability (requires admin)
if (!MftReader.IsAvailable())
{
    Console.WriteLine("MFT access requires administrator privileges");
    return;
}

// Create persistence and pipeline
await using var persistence = SqlitePersistence.CreateHighPerformance("index.db");
await persistence.InitializeAsync();

using var pipeline = new MftSqlitePipeline();

// Index all NTFS drives
var progress = new Progress<IndexingProgress>(p =>
    Console.WriteLine($"Indexed: {p.TotalIndexed:N0} - {p.CurrentOperation}"));

var total = await pipeline.IndexAllDrivesAsync(persistence, progress);

Console.WriteLine($"✅ Indexed {total:N0} files at {pipeline.Statistics.RecordsPerSecond:N0}/sec");
```

## USN Journal Real-Time Sync API

### UsnSqliteSyncService Class
Real-time file change synchronization using USN Journal.

```csharp
public class UsnSqliteSyncService : IAsyncDisposable
{
    // Start/Stop
    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StartAsync(char[] driveLetters, CancellationToken cancellationToken = default);
    public Task StopAsync();

    // Properties
    public bool IsRunning { get; }
    public SyncStatistics Statistics { get; }
}

public class SyncStatistics
{
    public long TotalChangesReceived { get; }
    public long Additions { get; }
    public long Updates { get; }
    public long Deletions { get; }
    public long Errors { get; }
    public TimeSpan Duration { get; }
    public double ChangesPerSecond { get; }
}
```

### Usage Example
```csharp
using FastFind.Windows.Mft;
using FastFind.SQLite;

// Initialize persistence
await using var persistence = SqlitePersistence.CreateHighPerformance("index.db");
await persistence.InitializeAsync();

// Start real-time sync
await using var syncService = new UsnSqliteSyncService(persistence);
await syncService.StartAsync(new[] { 'C', 'D' });

Console.WriteLine("Monitoring file changes...");

// Monitor statistics
while (syncService.IsRunning)
{
    var stats = syncService.Statistics;
    Console.WriteLine($"Changes: {stats.TotalChangesReceived:N0} ({stats.ChangesPerSecond:F1}/sec)");
    await Task.Delay(5000);
}
```
