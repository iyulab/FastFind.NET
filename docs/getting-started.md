# Getting Started with FastFind.NET

⚡ Ultra-high performance cross-platform file search library for .NET 9

## 📦 Installation

FastFind.NET provides platform-specific packages for optimal performance:

### Core Package (Required)
```bash
# .NET CLI
dotnet add package FastFind.Core

# Package Manager Console
Install-Package FastFind.Core
```

### Platform-Specific Packages

#### Windows (Recommended)
```bash
dotnet add package FastFind.Windows
```

#### Unix/Linux (🚧 Coming Soon)
```bash
# Will be available in future release
dotnet add package FastFind.Unix
```

## 🚀 Quick Start

### 1. Basic Setup

```csharp
using FastFind;
using Microsoft.Extensions.Logging;

// Create logger (optional)
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger<Program>();

// Validate system capabilities
var validation = FastFinder.ValidateSystem();
if (!validation.IsReady)
{
    logger.LogError("System validation failed: {Issues}", validation.GetSummary());
    return;
}

logger.LogInformation("✅ System ready: {Summary}", validation.GetSummary());
```

### 2. Create Search Engine

```csharp
// Create platform-optimized search engine
var searchEngine = FastFinder.CreateSearchEngine(logger);

// Configure indexing options
var indexingOptions = new IndexingOptions
{
    DriveLetters = ['C'], // Windows drives to scan
    ExcludedPaths = ["temp", "cache", "node_modules", ".git"],
    IncludeHidden = false,
    ParallelThreads = Environment.ProcessorCount
};

// Start background indexing
await searchEngine.StartIndexingAsync(indexingOptions);
```

### 3. Basic Search

```csharp
// Simple text search
var results = await searchEngine.SearchAsync("*.txt");

Console.WriteLine($"🔍 Found {results.TotalMatches} files in {results.SearchTime.TotalMilliseconds}ms");

foreach (var file in results.Files.Take(10))
{
    Console.WriteLine($"📄 {file.Name} ({file.SizeFormatted}) - {file.Directory}");
}
```

### 4. Advanced Search

```csharp
var query = new SearchQuery
{
    SearchText = "project",
    IncludeFiles = true,
    IncludeDirectories = false,
    ExtensionFilter = ".cs",
    MinSize = 1024, // 1KB minimum
    MaxSize = 1024 * 1024, // 1MB maximum
    UseRegex = false,
    CaseSensitive = false,
    MaxResults = 1000
};

var results = await searchEngine.SearchAsync(query);
Console.WriteLine($"🎯 Advanced search: {results.TotalMatches} matches");
```

## 📊 Monitoring Progress

### Indexing Progress

```csharp
searchEngine.IndexingProgressChanged += (sender, args) =>
{
    Console.WriteLine($"📂 Indexing: {args.ProcessedFiles:N0} files " +
                     $"({args.ProgressPercentage:F1}%) - {args.FilesPerSecond:F0} files/sec");
};
```

### Search Statistics

```csharp
var stats = await searchEngine.GetSearchStatisticsAsync();
Console.WriteLine($"📈 Performance: {stats.AverageSearchTime.TotalMilliseconds:F1}ms avg, " +
                 $"{stats.CacheHitRate:P1} cache hit rate");
```

## 🔧 Configuration Options

### Indexing Options

```csharp
var options = new IndexingOptions
{
    // Location settings
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

### Search Query Options

```csharp
var query = new SearchQuery
{
    SearchText = "search term",
    UseRegex = false,
    CaseSensitive = false,
    SearchFileNameOnly = true,
    
    // Filters
    ExtensionFilter = ".cs",
    MinSize = 1024,
    MaxSize = 1024 * 1024,
    MinCreatedDate = DateTime.Now.AddDays(-30),
    
    // Limits
    MaxResults = 1000
};
```

## 🌐 Platform Support

### Current Status

| Platform | Status | Package | Features |
|----------|--------|---------|----------|
| Windows | ✅ Available | FastFind.Windows | NTFS MFT, Junction Links, High Performance |
| Linux | 🚧 Roadmap | FastFind.Unix | ext4, inotify (planned) |
| macOS | 🚧 Roadmap | FastFind.Unix | APFS, FSEvents (planned) |

### Platform Detection

```csharp
var validation = FastFinder.ValidateSystem();

Console.WriteLine($"Platform: {validation.Platform}");
Console.WriteLine($"Available Features: {string.Join(", ", validation.AvailableFeatures)}");

if (validation.Platform == PlatformType.Windows)
{
    Console.WriteLine("✅ Windows-specific optimizations available");
}
else
{
    Console.WriteLine("ℹ️  Using cross-platform implementation");
}
```

## 🎯 Next Steps

1. **[API Reference](api-reference.md)** - Complete API documentation
2. **[Performance Guide](performance.md)** - Optimization techniques
3. **[Advanced Features](advanced-features.md)** - SIMD, Memory optimization
4. **[Troubleshooting](troubleshooting.md)** - Common issues and solutions

## 💡 Tips

- Use `FastFileItem` for memory-critical applications
- Configure appropriate batch sizes based on system specs
- Monitor memory usage with `StringPool.GetStats()`
- Enable logging to track performance and issues
- Use cancellation tokens for responsive UI applications