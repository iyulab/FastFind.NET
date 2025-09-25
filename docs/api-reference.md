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
