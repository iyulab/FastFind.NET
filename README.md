# FastFind.NET

Lightning-fast cross-platform file search library with native file system integration

[![NuGet Version](https://img.shields.io/nuget/v/FastFind.NET.svg)](https://www.nuget.org/packages/FastFind.NET)\
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## 🚀 Features

- **⚡ Lightning Fast**: Direct file system access for maximum performance
- **🌐 Cross-Platform**: Windows (NTFS), macOS (APFS), and Linux (ext4) support
- **🎯 Native Optimization**: Platform-specific file system integration
- **🔄 Real-time Updates**: Live search results as you type
- **📊 Advanced Filtering**: Size, date, attributes, regex patterns
- **💾 Memory Efficient**: Optimized in-memory indexing
- **🔧 Extensible**: Plugin architecture for custom file systems

## 📦 Installation

### NuGet Package Manager
```bash
Install-Package FastFind.NET
```

### .NET CLI
```bash
dotnet add package FastFind.NET
```

### PackageReference
```xml
<PackageReference Include="FastFind.NET" Version="1.0.0" />
```

## 🎯 Quick Start

### Basic Search
```csharp
using FastFind.Core;

// Create a search engine instance
var searchEngine = FastFind.CreateSearchEngine();

// Simple text search
var results = await searchEngine.SearchAsync("*.txt");

foreach (var file in results.Files)
{
    Console.WriteLine($"{file.Name} ({file.Size} bytes)");
}
```

### Advanced Search
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
    CaseSensitive = false
};

var results = await searchEngine.SearchAsync(query);
Console.WriteLine($"Found {results.TotalMatches} matches in {results.SearchTime.TotalMilliseconds}ms");
```

### Real-time Search
```csharp
var query = new SearchQuery { SearchText = "document" };

await foreach (var result in searchEngine.SearchRealTimeAsync(query))
{
    Console.WriteLine($"Updated: {result.TotalMatches} matches");
    // Results update as you modify the search text
}
```

## 🏗️ Architecture

FastFind.NET uses a modular architecture with platform-specific optimizations:

```
FastFind.NET
├── FastFind.Core              # Common interfaces and models
├── FastFind.Windows           # Windows/NTFS implementation
└── FastFind.Unix              # macOS and Linux implementation
    ├── Platforms/
    │   ├── MacOSProvider.cs   # APFS optimizations
    │   └── LinuxProvider.cs   # ext4 optimizations
    └── UnixSearchEngine.cs    # Common Unix logic
```

## 🔧 Configuration

### Indexing Options
```csharp
var options = new IndexingOptions
{
    DriveLetters = new[] { 'C', 'D' }, // Windows
    MountPoints = new[] { "/", "/home" }, // Unix
    ExcludedPaths = new[] { "temp", "cache" },
    IncludeHidden = false,
    IncludeSystem = false,
    MaxFileSize = 100 * 1024 * 1024 // 100MB
};

await searchEngine.StartIndexingAsync(options);
```

### Performance Tuning
```csharp
var settings = new SearchSettings
{
    MaxResults = 1000,
    DebounceDelay = TimeSpan.FromMilliseconds(150),
    EnableCache = true,
    CacheSize = 10000
};

searchEngine.Configure(settings);
```

## 📊 Performance

FastFind.NET delivers exceptional performance across all platforms:

| Operation | Windows (NTFS) | macOS (APFS) | Linux (ext4) |
|-----------|----------------|--------------|--------------|
| Index 1M files | ~30 seconds | ~45 seconds | ~35 seconds |
| Search (text) | <10ms | <15ms | <12ms |
| Search (regex) | <50ms | <75ms | <60ms |
| Memory usage | ~500MB | ~600MB | ~550MB |

*Benchmarks run on modern hardware with SSD storage*

## 🔍 Search Features

### Text Search
- Wildcard patterns (`*.txt`, `photo*`)
- Case-sensitive/insensitive matching
- Full path or filename-only search

### Regular Expressions
```csharp
var query = new SearchQuery
{
    SearchText = @"^IMG_\d{4}\.jpg$",
    UseRegex = true
};
```

### Advanced Filters
```csharp
var query = new SearchQuery
{
    // Size filters
    MinSize = 1024 * 1024, // 1MB
    MaxSize = 100 * 1024 * 1024, // 100MB
    
    // Date filters
    MinCreatedDate = DateTime.Now.AddDays(-30),
    MaxModifiedDate = DateTime.Now,
    
    // Attribute filters
    IncludeHidden = false,
    IncludeSystem = false,
    
    // Type filters
    ExtensionFilter = ".pdf"
};
```

## 🎮 Platform-Specific Features

### Windows (NTFS)
- Direct Master File Table (MFT) access
- NTFS junction and symbolic link support
- Windows file attributes integration

### macOS (APFS)
- Core Foundation integration
- FSEvents for real-time monitoring
- APFS snapshot awareness

### Linux (ext4)
- inotify for file system monitoring
- Extended attribute support
- Multiple file system compatibility

## 📈 Monitoring & Statistics

```csharp
// Get search statistics
var stats = await searchEngine.GetSearchStatisticsAsync();
Console.WriteLine($"Total searches: {stats.TotalSearches}");
Console.WriteLine($"Average time: {stats.AverageSearchTime}");
Console.WriteLine($"Cache hit rate: {stats.CacheHitRate:P}");

// Get indexing progress
searchEngine.IndexingProgressChanged += (sender, args) =>
{
    Console.WriteLine($"Indexed {args.ProcessedFiles} files on drive {args.DriveLetter}");
};
```

## 🔧 API Reference

### Core Interfaces

#### ISearchEngine
```csharp
public interface ISearchEngine
{
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SearchResult> SearchRealTimeAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task StartIndexingAsync(IndexingOptions options, CancellationToken cancellationToken = default);
    Task<SearchStatistics> GetSearchStatisticsAsync();
}
```

#### SearchQuery
```csharp
public class SearchQuery
{
    public string SearchText { get; set; }
    public bool UseRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public bool SearchFileNameOnly { get; set; }
    public string? ExtensionFilter { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateTime? MinCreatedDate { get; set; }
    public DateTime? MaxCreatedDate { get; set; }
    public int? MaxResults { get; set; }
}
```
