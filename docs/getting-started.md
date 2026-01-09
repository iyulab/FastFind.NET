# Getting Started with FastFind.NET

⚡ Ultra-high performance cross-platform file search library for .NET 10

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

#### Windows (Production Ready) ✅
```bash
dotnet add package FastFind.Windows
```
**Performance**: 1.87M SIMD ops/sec, 500K+ MFT files/sec, 61-byte FastFileItem structs

#### SQLite Persistence 🗄️
```bash
dotnet add package FastFind.SQLite
```
**Features**: FTS5 full-text search, WAL mode, 100K+ bulk inserts/sec

#### Unix/Linux (🚧 Coming Soon)
```bash
# Will be available in future release
dotnet add package FastFind.Unix
```

## 🚀 Quick Start

### 1. Basic Setup with Async Optimization

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

### 2. Create Search Engine with Async Disposal

```csharp
// Windows factory is auto-registered via ModuleInitializer (no manual registration needed)
// WindowsRegistration.EnsureRegistered(); // Optional: only if you need explicit control

// Create platform-optimized search engine with async disposal
await using var searchEngine = FastFinder.CreateWindowsSearchEngine(loggerFactory);

// Configure indexing options with async optimization
var indexingOptions = new IndexingOptions
{
    DriveLetters = ['C'], // Windows drives to scan
    ExcludedPaths = ["temp", "cache", "node_modules", ".git"],
    IncludeHidden = false,
    ParallelThreads = Environment.ProcessorCount,
    UseMemoryPool = true,    // New: MemoryPool<T> optimization
    UseAsyncIO = true        // New: True async I/O with IOCP
};

// Start background indexing
await searchEngine.StartIndexingAsync(indexingOptions).ConfigureAwait(false);
```

### 3. Basic Search with Streaming

```csharp
// Simple text search with streaming results
var results = await searchEngine.SearchAsync("*.txt").ConfigureAwait(false);

Console.WriteLine($"🔍 Found {results.TotalMatches} files in {results.SearchTime.TotalMilliseconds}ms");

// Stream results for memory efficiency - New async pattern
await foreach (var file in results.Files.ConfigureAwait(false))
{
    Console.WriteLine($"📄 {file.Name} ({file.Size:N0} bytes) - {file.Directory}");

    // Process files immediately without buffering
    if (file.Extension == ".txt" && file.Size > 1024)
    {
        // Process large text files
        break; // Example: stop after first large file
    }
}
```

### 4. Enhanced Search with Path Control ⚡

```csharp
// New: Enhanced search with base path and subdirectory control
var query = new SearchQuery
{
    // 🎯 Enhanced Path-Based Search Options
    BasePath = @"D:\Projects",           // 기준경로: Search from specific base path
    SearchText = "Controller",           // search-text: Pattern in paths/filenames
    IncludeSubdirectories = true,        // subdirectory: Include subdirectories
    SearchFileNameOnly = false,          // Search in full paths (default: false)

    // 📂 File Filters
    IncludeFiles = true,
    IncludeDirectories = false,
    ExtensionFilter = ".cs",

    // 📏 Size Filters
    MinSize = 1024, // 1KB minimum
    MaxSize = 1024 * 1024, // 1MB maximum

    // ⚙️ Search Behavior
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
    // Enhanced search options
    BasePath = @"C:\MyProject",          // Start from specific directory
    SearchText = "search term",          // Pattern to find
    IncludeSubdirectories = true,        // Include subdirectories
    SearchFileNameOnly = false,          // Search in full paths (recommended)

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

## 🌐 Platform Support

### Current Status

| Platform | Status | Package | Verified Performance |
|----------|--------|---------|----------------------|
| Windows | ✅ Production | FastFind.Windows | 1.87M SIMD ops/sec, 243K files/sec indexing, 61-byte structs |
| Linux | 🚧 Roadmap | FastFind.Unix | ext4, inotify (planned Q2 2025) |
| macOS | 🚧 Roadmap | FastFind.Unix | APFS, FSEvents (planned Q2 2025) |

### Platform Detection

```csharp
var validation = FastFinder.ValidateSystem();

Console.WriteLine($"Platform: {validation.Platform}");
Console.WriteLine($"Available Features: {string.Join(", ", validation.AvailableFeatures)}");
Console.WriteLine($"Performance: SIMD={StringMatchingStats.SIMDUsagePercentage:F0}%, StringPool={StringPool.GetStats().CompressionRatio:P0} compression");

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
2. **[Search Examples](search-examples.md)** - Practical search scenarios
3. **[Roadmap](roadmap.md)** - Future plans and development status

### Advanced Features (v1.0.8+)
- **MFT Direct Access** - Ultra-fast NTFS enumeration (500K+ files/sec)
- **SQLite Persistence** - Persistent index with FTS5 full-text search
- **USN Journal Sync** - Real-time file change detection

## 📊 Performance Monitoring

### Real-Time Performance Metrics

```csharp
// SIMD String Matching Statistics
var simdStats = StringMatchingStats.SIMDUsagePercentage;
Console.WriteLine($"SIMD Utilization: {simdStats:F1}%"); // Target: >90%

// String Pool Efficiency
var poolStats = StringPool.GetStats();
Console.WriteLine($"Memory Compression: {poolStats.CompressionRatio:P1}"); // Target: >60%
Console.WriteLine($"Pool Size: {poolStats.MemoryUsageMB:F1}MB");

// LazyFormatCache Performance
var (hits, misses, total, hitRatio) = LazyFormatCache.GetCacheStats();
Console.WriteLine($"Cache Hit Ratio: {hitRatio:P1}"); // Target: >80%
```

### Benchmark Your System

```csharp
// Create test data
var testItems = GenerateTestFileItems(10000);

// Measure FastFileItem creation
var stopwatch = Stopwatch.StartNew();
var fastItems = testItems.ToFastFileItemsBatch().ToArray();
stopwatch.Stop();

Console.WriteLine($"FastFileItem Creation: {fastItems.Length / stopwatch.Elapsed.TotalSeconds:N0} items/sec");
// Target: >200K items/sec

// Measure SIMD search performance
var searchTerm = "test";
var searchCount = 0;
stopwatch.Restart();

for (int i = 0; i < 100000; i++)
{
    if (fastItems[i % fastItems.Length].MatchesName(searchTerm))
        searchCount++;
}

stopwatch.Stop();
Console.WriteLine($"SIMD Search: {100000 / stopwatch.Elapsed.TotalSeconds:N0} ops/sec");
// Target: >1M ops/sec
```

## 💡 Performance Tips

- **Use FastFileItem** for memory-critical applications (61 bytes vs 200+ bytes)
- **Enable SIMD**: Verify 100% SIMD utilization on compatible hardware
- **Monitor StringPool**: Aim for >60% memory compression ratio
- **Configure batch sizes** based on system specs (default: 1000)
- **Use cancellation tokens** for responsive UI applications
- **Check LazyFormatCache**: Monitor hit ratio >80% for UI scenarios

### Verified Performance Targets
- **SIMD Operations**: >1,000,000 ops/sec
- **File Indexing**: >100,000 files/sec
- **FastFileItem Creation**: >200,000 items/sec  
- **StringPool Compression**: >60% memory reduction
- **Cache Hit Ratio**: >80% for UI formatting

## 🧪 Running Tests

### Functional Tests (CI/CD Safe)
```bash
# Standard tests without heavy performance testing
dotnet test --filter "Category!=Performance"
```

### Performance Tests (Manual Only)
```bash
# WARNING: These tests can take 30-120 minutes!

# All performance tests
dotnet test --filter "Category=Performance"

# Specific performance suites
dotnet test --filter "Suite:SIMD"      # SIMD string matching tests
dotnet test --filter "Suite:StringPool" # String interning tests
dotnet test --filter "Suite:Integration" # Integration performance tests
dotnet test --filter "Suite:Stress"     # Large dataset stress tests

# Set test duration (optional)
$env:PERFORMANCE_TEST_DURATION="Quick"   # 5-10 min
$env:PERFORMANCE_TEST_DURATION="Standard" # 30-45 min  
$env:PERFORMANCE_TEST_DURATION="Extended" # 1-2 hours
```

### BenchmarkDotNet (Most Comprehensive)
```bash
# Run professional benchmarks with statistical analysis
dotnet run --project tests/FastFind.Windows.Tests --configuration Release

# In the test runner, call:
# BenchmarkRunner.RunSearchBenchmarks()
```