# Getting Started with FastFind.NET

## Installation

```bash
dotnet add package FastFind.Core

# Platform-specific (auto-registered at runtime via ModuleInitializer)
dotnet add package FastFind.Windows    # Windows
dotnet add package FastFind.Unix       # Linux / macOS

dotnet add package FastFind.SQLite     # Optional: SQLite persistence
```

See [README](../README.md) for full package list and version badges.

## Quick Start (Windows)

### 1. Create Search Engine

```csharp
using FastFind;
using Microsoft.Extensions.Logging;

// Create logger (optional)
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));

// Windows factory is auto-registered via ModuleInitializer
using var searchEngine = FastFinder.CreateWindowsSearchEngine(loggerFactory);
```

### 2. Build Index

```csharp
await searchEngine.StartIndexingAsync(new IndexingOptions
{
    DriveLetters = ['C', 'D'],
    ExcludedPaths = ["node_modules", "bin", "obj", ".git"],
    CollectFileSize = true,  // Enable file size collection (default: false for max speed)
    ParallelThreads = Environment.ProcessorCount
});

// Wait for indexing to complete
while (searchEngine.IsIndexing)
{
    await Task.Delay(500);
}
```

### 3. Search

```csharp
var results = await searchEngine.SearchAsync(new SearchQuery
{
    BasePath = @"D:\Projects",
    SearchText = "Controller",
    ExtensionFilter = ".cs",
    IncludeSubdirectories = true,
    MaxResults = 100
});

// Stream results for memory efficiency
await foreach (var file in results.Files)
{
    Console.WriteLine($"{file.Name} ({file.SizeFormatted}) - {file.DirectoryPath}");
}
```

## Quick Start (Linux)

### 1. Create Search Engine

```csharp
using FastFind;
using FastFind.Unix;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));

// Linux factory is auto-registered via ModuleInitializer
using var searchEngine = UnixSearchEngine.CreateLinuxSearchEngine(loggerFactory);
```

### 2. Build Index

```csharp
await searchEngine.StartIndexingAsync(new IndexingOptions
{
    MountPoints = ["/home", "/opt", "/var"],
    ExcludedPaths = ["node_modules", ".git", "__pycache__", ".cache"],
    CollectFileSize = true,
    ParallelThreads = Environment.ProcessorCount
});

while (searchEngine.IsIndexing)
{
    await Task.Delay(500);
}
```

### 3. Search

```csharp
var results = await searchEngine.SearchAsync(new SearchQuery
{
    BasePath = "/home/user/projects",
    SearchText = "config",
    ExtensionFilter = ".json",
    IncludeSubdirectories = true,
    MaxResults = 100
});

await foreach (var file in results.Files)
{
    Console.WriteLine($"{file.Name} - {file.DirectoryPath}");
}
```

## Configuration

### IndexingOptions

```csharp
var options = new IndexingOptions
{
    // Location
    DriveLetters = ['C', 'D'],
    SpecificDirectories = [@"C:\Projects", @"D:\Documents"],

    // Filtering
    ExcludedPaths = ["temp", "cache", "node_modules", ".git", "bin", "obj"],
    ExcludedExtensions = [".tmp", ".cache", ".log"],
    IncludeHidden = false,
    IncludeSystem = false,

    // Performance
    MaxFileSize = 100 * 1024 * 1024, // 100MB
    ParallelThreads = Environment.ProcessorCount,
    BatchSize = 1000
};
```

### SearchQuery

```csharp
var query = new SearchQuery
{
    // Path options
    BasePath = @"C:\MyProject",
    SearchText = "search term",
    IncludeSubdirectories = true,
    SearchFileNameOnly = false,       // false = search full paths (default)

    // Search behavior
    UseRegex = false,
    CaseSensitive = false,

    // Filters
    ExtensionFilter = ".cs",
    MinSize = 1024,
    MaxSize = 1024 * 1024,
    MinCreatedDate = DateTime.Now.AddDays(-30),

    // Limits
    MaxResults = 1000
};
```

## Monitoring Progress

### Indexing Progress

```csharp
searchEngine.IndexingProgressChanged += (sender, args) =>
{
    Console.WriteLine($"Indexing: {args.ProcessedFiles:N0} files " +
                     $"({args.ProgressPercentage:F1}%) - {args.FilesPerSecond:F0} files/sec");
};
```

### Search Statistics

```csharp
var stats = await searchEngine.GetSearchStatisticsAsync();
Console.WriteLine($"Performance: {stats.AverageSearchTime.TotalMilliseconds:F1}ms avg, " +
                 $"{stats.CacheHitRate:P1} cache hit rate");
```

## System Validation

```csharp
var validation = FastFinder.ValidateSystem();
if (!validation.IsReady)
{
    Console.WriteLine($"System validation failed: {validation.GetSummary()}");
    return;
}

Console.WriteLine($"Platform: {validation.Platform}");
Console.WriteLine($"Available Features: {string.Join(", ", validation.AvailableFeatures)}");
```

## Performance Tips

- **Use `BasePath`** for targeted searches instead of system-wide `SearchLocations`
- **Set `IncludeSubdirectories = false`** when you only need direct files
- **Use `SearchFileNameOnly = true`** for faster filename-only searches
- **Set `MaxResults`** to limit memory usage for large result sets
- **Use `CollectFileSize = false`** (default) for maximum indexing speed
- **Use cancellation tokens** for responsive UI applications

## Platform Detection

FastFind.NET automatically detects the platform and registers the appropriate search engine factory:

```csharp
// Platform-agnostic creation (requires platform package reference)
var validation = FastFinder.ValidateSystem();
Console.WriteLine($"Platform: {validation.Platform}");
// Windows → PlatformType.Windows
// Linux   → PlatformType.Linux
```

The `ModuleInitializer` in each platform package (`FastFind.Windows`, `FastFind.Unix`) automatically registers itself when the assembly is loaded. No manual registration is needed.

## Next Steps

- [API Reference](api-reference.md) - Complete API documentation
- [Search Examples](search-examples.md) - Practical search scenarios
- [Performance Benchmarks](BENCHMARKS.md) - Detailed performance data
- [Roadmap](roadmap.md) - Future plans and development status
