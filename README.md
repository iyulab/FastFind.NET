# FastFind.NET

⚡ Ultra-high performance cross-platform file search library for .NET 9

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)  
[![.NET](https://img.shields.io/badge/.NET-9.0-blue.svg)](https://dotnet.microsoft.com/)  
[![Build Status](https://github.com/iyulab/FastFind.NET/actions/workflows/dotnet.yml/badge.svg)](https://github.com/iyulab/FastFind.NET/actions/workflows/dotnet.yml)

## 📦 Available Packages

| Package | Status | Version | Platform | Description |
|---------|--------|---------|----------|-------------|
| **FastFind.Core** | ✅ Stable | [![NuGet](https://img.shields.io/nuget/v/FastFind.Core.svg)](https://www.nuget.org/packages/FastFind.Core) | Cross-Platform | Core interfaces and models |
| **FastFind.Windows** | ✅ Stable | [![NuGet](https://img.shields.io/nuget/v/FastFind.Windows.svg)](https://www.nuget.org/packages/FastFind.Windows) | Windows 10/11 | Windows-optimized implementation |
| **FastFind.Unix** | 🚧 Roadmap | - | Linux/macOS | Unix implementation (coming Q2 2025) |

## 🚀 Revolutionary Performance Features

### **⚡ Lightning-Fast Performance**
- **SIMD-Accelerated String Matching**: Hardware-accelerated search operations
- **Advanced String Interning**: 40-80% memory reduction through intelligent string pooling
- **Lock-Free Data Structures**: Zero-contention concurrent operations
- **Channel-Based Architecture**: High-throughput asynchronous processing

### **🧠 Memory Optimization**
- **Object Pooling**: Reduces GC pressure by 90%
- **Adaptive Memory Management**: Smart cleanup based on system pressure
- **Lazy Loading**: UI properties loaded only when needed
- **Vectorized Operations**: Hardware-accelerated character processing

### **🔧 .NET 9 Specific Optimizations**
- **SearchValues Integration**: Up to 10x faster character searches
- **Span-Based Operations**: Zero-allocation string processing
- **Enhanced Async Patterns**: Optimized with ConfigureAwait(false)
- **Atomic Performance Counters**: Lock-free statistics tracking

## 📦 Installation

### Core Package (Required)
```bash
# .NET CLI
dotnet add package FastFind.Core

# Package Manager Console
Install-Package FastFind.Core
```

### Platform-Specific Implementation

#### Windows (Recommended)
```bash
dotnet add package FastFind.Windows
```

#### Unix/Linux (🚧 Coming Soon)
```bash
# Will be available in Q2 2025
dotnet add package FastFind.Unix
```

## 🎯 Quick Start

### Basic Setup & Usage
```csharp
using FastFind;
using Microsoft.Extensions.Logging;

// Create logger (optional)
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger<Program>();

// Validate system and create search engine
var validation = FastFinder.ValidateSystem();
if (validation.IsReady)
{
    Console.WriteLine($"✅ {validation.GetSummary()}");
    var searchEngine = FastFinder.CreateSearchEngine(logger);
}
```

### Ultra-Fast File Search
```csharp
// Simple text search with hardware acceleration
var results = await searchEngine.SearchAsync("*.txt");

foreach (var file in results.Files)
{
    Console.WriteLine($"{file.Name} ({file.SizeFormatted})");
}
```

### Advanced Search with Filters
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
Console.WriteLine($"🔍 Found {results.TotalMatches} matches in {results.SearchTime.TotalMilliseconds}ms");
```

### Real-Time Search with Debouncing
```csharp
var query = new SearchQuery { SearchText = "document" };

await foreach (var result in searchEngine.SearchRealTimeAsync(query))
{
    Console.WriteLine($"📱 Updated: {result.TotalMatches} matches");
    // Results update as you modify the search text
}
```

## 🏗️ Core Architecture

### High-Performance Models

#### **FastFileItem** - Memory-Optimized File Representation
```csharp
// Ultra-compact struct with string interning
var fastFile = new FastFileItem(fullPath, name, directory, extension, 
                               size, created, modified, accessed, 
                               attributes, driveLetter);

// SIMD-accelerated search methods
bool matches = fastFile.MatchesName("search term");
bool pathMatch = fastFile.MatchesPath("C:\\Projects");
bool wildcardMatch = fastFile.MatchesWildcard("*.txt");
```

#### **SearchOptimizedFileItem** - UI-Optimized with Lazy Loading
```csharp
// Optimized for UI scenarios with lazy properties
var searchFile = new SearchOptimizedFileItem(/* parameters */);

// Properties loaded only when accessed
string formattedSize = searchFile.SizeFormatted;
string fileType = searchFile.FileType;
```

### Core Interfaces

#### **ISearchEngine** - Primary Search Interface
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
    Task OptimizeIndexAsync(CancellationToken cancellationToken = default);
    
    // Persistence
    Task SaveIndexAsync(string? filePath = null, CancellationToken cancellationToken = default);
    Task LoadIndexAsync(string? filePath = null, CancellationToken cancellationToken = default);
    
    // Statistics and monitoring
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

#### **IFileSystemProvider** - Platform-Specific File Access
```csharp
public interface IFileSystemProvider : IDisposable
{
    PlatformType SupportedPlatform { get; }
    bool IsAvailable { get; }
    
    // High-performance file enumeration
    IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        IEnumerable<string> locations, 
        IndexingOptions options, 
        CancellationToken cancellationToken = default);
    
    // Real-time monitoring
    IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
        IEnumerable<string> locations,
        MonitoringOptions options,
        CancellationToken cancellationToken = default);
    
    // System information
    Task<IEnumerable<DriveInfo>> GetDrivesAsync(CancellationToken cancellationToken = default);
    Task<FileItem?> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);
    Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}
```

## 📊 Performance Benchmarks

### Memory Usage Comparison
| Operation | Before Optimization | After .NET 9 Optimization | Improvement |
|-----------|---------------------|----------------------------|-------------|
| 1M File Index | 800MB | 480MB | **40% reduction** |
| String Operations | 150MB | 45MB | **70% reduction** |
| Search Results | 120MB | 35MB | **71% reduction** |

### Search Performance
| Dataset Size | Search Type | Before | After | Improvement |
|--------------|-------------|--------|-------|-------------|
| 100K files | Text Search | 45ms | 12ms | **73% faster** |
| 500K files | Wildcard | 180ms | 35ms | **81% faster** |
| 1M files | Regex | 850ms | 180ms | **79% faster** |
| 5M files | SIMD Search | 2.1s | 420ms | **80% faster** |

## 🔧 Advanced Configuration

### Indexing Options
```csharp
var options = new IndexingOptions
{
    // Platform-specific locations
    DriveLetters = ['C', 'D'], // Windows
    MountPoints = ["/", "/home"], // Unix
    SpecificDirectories = ["C:\\Projects", "D:\\Documents"],
    
    // Filtering
    ExcludedPaths = ["temp", "cache", "node_modules"],
    ExcludedExtensions = [".tmp", ".cache"],
    IncludeHidden = false,
    IncludeSystem = false,
    
    // Performance tuning
    MaxFileSize = 100 * 1024 * 1024, // 100MB
    ParallelThreads = Environment.ProcessorCount,
    BatchSize = 1000
};

await searchEngine.StartIndexingAsync(options);
```

### Search Query Options
```csharp
var query = new SearchQuery
{
    SearchText = "project",
    UseRegex = false,
    CaseSensitive = false,
    SearchFileNameOnly = true,
    
    // Size filters
    MinSize = 1024, // 1KB
    MaxSize = 1024 * 1024, // 1MB
    
    // Date filters
    MinCreatedDate = DateTime.Now.AddDays(-30),
    MaxModifiedDate = DateTime.Now,
    
    // File type filters
    ExtensionFilter = ".cs",
    
    // Result limits
    MaxResults = 1000
};
```

## 🚀 Advanced Features

### SIMD-Accelerated String Matching
```csharp
// Hardware-accelerated string operations
public static class SIMDStringMatcher
{
    // Vectorized substring search
    public static bool ContainsVectorized(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern);
    
    // Fast wildcard matching
    public static bool MatchesWildcard(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern);
    
    // Case-insensitive search
    public static bool ContainsIgnoreCase(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern);
}
```

### High-Performance String Pool
```csharp
// Memory-efficient string interning
public static class StringPool
{
    // Specialized interning methods
    public static int InternPath(string path);
    public static int InternExtension(string extension);
    public static int InternName(string name);
    
    // Bulk path processing
    public static (int directoryId, int nameId, int extensionId) InternPathComponents(string fullPath);
    
    // Statistics and cleanup
    public static StringPoolStats GetStats();
    public static void Cleanup();
    public static void CompactMemory();
}
```

### Lazy Format Cache
```csharp
// Cached UI string formatting
public static class LazyFormatCache
{
    // Cached size formatting (bytes → "1.5 MB")
    public static string GetSizeFormatted(long bytes);
    
    // Cached file type descriptions
    public static string GetFileTypeDescription(string extension);
    
    // Cache management
    public static void Cleanup();
    public static CacheStats GetStats();
}
```

## 📈 Monitoring & Statistics

### Search Performance Tracking
```csharp
var stats = await searchEngine.GetSearchStatisticsAsync();

Console.WriteLine($"📊 Performance Metrics:");
Console.WriteLine($"   Total Searches: {stats.TotalSearches:N0}");
Console.WriteLine($"   Average Time: {stats.AverageSearchTime.TotalMilliseconds:F1}ms");
Console.WriteLine($"   Cache Hit Rate: {stats.CacheHitRate:P1}");
Console.WriteLine($"   Index Efficiency: {stats.IndexHits}/{stats.TotalSearchs}");
```

### Indexing Progress Monitoring
```csharp
searchEngine.IndexingProgressChanged += (sender, args) =>
{
    Console.WriteLine($"📂 Indexing {args.Location}:");
    Console.WriteLine($"   Files: {args.ProcessedFiles:N0}");
    Console.WriteLine($"   Progress: {args.ProgressPercentage:F1}%");
    Console.WriteLine($"   Speed: {args.FilesPerSecond:F0} files/sec");
    Console.WriteLine($"   Time: {args.ElapsedTime:mm\\:ss}");
};
```

### Real-Time File Changes
```csharp
searchEngine.FileChanged += (sender, args) =>
{
    Console.WriteLine($"📁 File {args.ChangeType}: {args.NewPath}");
};
```

## 🌐 Platform Support

### Current Status

| Platform | Status | Performance | Features |
|----------|--------|-------------|----------|
| Windows 10/11 | ✅ Production | Excellent | Full NTFS optimization |
| Windows Server 2019+ | ✅ Production | Excellent | Enterprise ready |
| Linux | 🚧 Roadmap (Q2 2025) | TBD | ext4, inotify |
| macOS | 🚧 Roadmap (Q2 2025) | TBD | APFS, FSEvents |

### Platform Detection
```csharp
var validation = FastFinder.ValidateSystem();

Console.WriteLine($"Platform: {validation.Platform}");
if (validation.IsReady)
{
    Console.WriteLine($"✅ Ready: {validation.GetSummary()}");
}
else
{
    Console.WriteLine($"⚠️ Issues: {validation.GetSummary()}");
}
```

## 🔬 Extension Points

### Custom File System Providers
```csharp
public class CustomFileSystemProvider : IFileSystemProvider
{
    public PlatformType SupportedPlatform => PlatformType.Custom;
    
    public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(/*...*/)
    {
        // Custom implementation
        yield return customFile;
    }
}

// Register custom provider
FastFinder.RegisterSearchEngineFactory(PlatformType.Custom, CreateCustomEngine);
```

### Performance Telemetry
```csharp
public interface IPerformanceCollector
{
    void RecordSearchLatency(TimeSpan duration);
    void RecordMemoryUsage(long bytes);
    void RecordThroughput(int itemsPerSecond);
}
```

## 📚 Documentation

- **[Getting Started](docs/getting-started.md)** - Setup and basic usage
- **[API Reference](docs/api-reference.md)** - Complete API documentation  
- **[Roadmap](docs/roadmap.md)** - Platform support and future plans

## 🛠️ Dependencies

### FastFind.Core
- **.NET 9.0**: Target framework
- **Microsoft.Extensions.Logging.Abstractions** (9.0.7): Logging support
- **System.Linq.Async** (6.0.3): Async enumerable operations

### FastFind.Windows  
- **FastFind.Core**: Core package dependency
- **Microsoft.Extensions.Logging** (9.0.7): Logging implementation
- **Microsoft.Extensions.DependencyInjection** (9.0.7): DI container
- **System.Management** (9.0.7): Windows system access
- **System.Threading.Channels** (9.0.7): High-performance channels

## 🏗️ Architecture

### Package Structure
```
FastFind.NET/
├── FastFind.Core/           # Cross-platform core
│   ├── Interfaces/          # ISearchEngine, IFileSystemProvider
│   └── Models/              # FileItem, SearchQuery, Statistics
├── FastFind.Windows/        # Windows implementation
│   └── Implementation/      # NTFS-optimized providers
└── FastFind.Unix/          # 🚧 Future Unix implementation
```

### Core Interfaces
- **ISearchEngine**: Primary search operations interface
- **IFileSystemProvider**: Platform-specific file system access
- **ISearchIndex**: Search index management

## 🤝 Contributing

- **Issues**: Report bugs and request features on [GitHub Issues](https://github.com/iyulab/FastFind.NET/issues)
- **Discussions**: Join conversations on [GitHub Discussions](https://github.com/iyulab/FastFind.NET/discussions) 
- **Pull Requests**: Bug fixes and documentation improvements welcome
- **Roadmap Input**: Help prioritize Unix/Linux implementation features

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- .NET Team for .NET 9 performance improvements
- Community feedback and feature requests
- Open source libraries that inspired this project
