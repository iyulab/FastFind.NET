# Getting Started with FastFind.NET

## Installation

```bash
dotnet add package FastFind.Core

# Platform-specific (auto-registered at runtime via ModuleInitializer)
dotnet add package FastFind.Windows    # Windows
dotnet add package FastFind.Unix       # Linux / macOS

dotnet add package FastFind.SQLite     # Optional: SQLite persistence
```

## Quick Start

```csharp
using FastFind;

// Platform auto-detected — creates Windows, Linux, or macOS engine
using var engine = FastFinder.CreateSearchEngine();

// Index target directories
await engine.StartIndexingAsync(new IndexingOptions
{
    SpecificDirectories = [@"D:\Projects"],       // Windows
    // MountPoints = ["/home", "/opt"],           // Linux
    ExcludedPaths = ["node_modules", ".git", "bin", "obj"],
    CollectFileSize = true
});

while (engine.IsIndexing) await Task.Delay(500);

// Search
var results = await engine.SearchAsync(new SearchQuery
{
    BasePath = @"D:\Projects",
    SearchText = "controller",
    ExtensionFilter = ".cs",
    MaxResults = 100
});

await foreach (var file in results.Files)
{
    Console.WriteLine($"{file.Name} ({file.SizeFormatted}) - {file.DirectoryPath}");
}
```

## Configuration

### IndexingOptions

```csharp
var options = new IndexingOptions
{
    // Location (choose one)
    DriveLetters = ['C', 'D'],                              // Windows: drive letters
    SpecificDirectories = [@"C:\Projects", @"D:\Documents"], // Windows: specific dirs
    MountPoints = ["/home", "/opt"],                         // Linux/macOS: mount points

    // Filtering
    ExcludedPaths = ["node_modules", ".git", "bin", "obj"],
    ExcludedExtensions = [".tmp", ".cache", ".log"],
    IncludeHidden = false,      // Linux/macOS: dotfiles, Windows: Hidden attribute
    IncludeSystem = false,      // Windows only (no effect on Linux/macOS)

    // Performance
    CollectFileSize = false,    // false = max indexing speed
    MaxFileSize = 100 * 1024 * 1024,
    ParallelThreads = Environment.ProcessorCount,
    BatchSize = 1000
};
```

### SearchQuery

```csharp
var query = new SearchQuery
{
    BasePath = @"D:\Projects",
    SearchText = "search term",
    IncludeSubdirectories = true,
    SearchFileNameOnly = false,       // false = search full paths

    // Search behavior
    UseRegex = false,
    CaseSensitive = false,

    // Filters
    ExtensionFilter = ".cs",
    MinSize = 1024,
    MaxSize = 1024 * 1024,
    MinModifiedDate = DateTime.Now.AddDays(-30),

    // Limits
    MaxResults = 1000
};
```

## Search Examples

### Regex Pattern Search
```csharp
var results = await engine.SearchAsync(new SearchQuery
{
    BasePath = @"D:\Projects\WebApp",
    SearchText = @"Service|Repository|Controller",
    UseRegex = true,
    ExtensionFilter = ".cs",
    IncludeSubdirectories = true
});
```

### Date & Size Filtering
```csharp
// Recent large files
var results = await engine.SearchAsync(new SearchQuery
{
    BasePath = @"D:\Downloads",
    MinSize = 100 * 1024 * 1024,      // > 100MB
    MinModifiedDate = DateTime.Now.AddDays(-7),
    IncludeSubdirectories = true
});
```

### Streaming vs Batch Collection
```csharp
// Streaming (memory efficient)
await foreach (var file in results.Files)
{
    await ProcessFileAsync(file);
}

// Batch (when you need all results)
var allFiles = new List<FastFileItem>();
await foreach (var file in results.Files)
{
    allFiles.Add(file);
}
```

### Linux/macOS-Specific
```csharp
// Find nginx config files
var results = await engine.SearchAsync(new SearchQuery
{
    BasePath = "/etc",
    SearchText = "nginx",
    ExtensionFilter = ".conf",
    IncludeSubdirectories = true
});

// Find large log files
var results = await engine.SearchAsync(new SearchQuery
{
    BasePath = "/var/log",
    MinSize = 100 * 1024 * 1024,
    ExtensionFilter = ".log",
    IncludeSubdirectories = true
});
```

## Monitoring & Validation

```csharp
// Indexing progress
engine.IndexingProgressChanged += (sender, args) =>
{
    Console.WriteLine($"Indexing: {args.ProcessedFiles:N0} files ({args.ProgressPercentage:F1}%)");
};

// System validation
var validation = FastFinder.ValidateSystem();
Console.WriteLine($"Platform: {validation.Platform}, Ready: {validation.IsReady}");
```

## Performance Tips

- **Use `BasePath`** for targeted searches instead of system-wide `SearchLocations`
- **Set `IncludeSubdirectories = false`** when you only need direct files
- **Use `SearchFileNameOnly = true`** for faster filename-only searches
- **Set `MaxResults`** to limit memory usage for large result sets
- **Use `CollectFileSize = false`** (default) for maximum indexing speed

## Next Steps

- [API Reference](api-reference.md) — Interface and class signatures
- [Performance Benchmarks](BENCHMARKS.md) — Detailed performance data
- [Roadmap](roadmap.md) — Development plans
