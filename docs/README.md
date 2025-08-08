# FastFind.NET Documentation

📖 Comprehensive documentation for FastFind.NET - Ultra-high performance file search library

## 📋 Table of Contents

### Getting Started
- **[Getting Started Guide](getting-started.md)** - Installation, setup, and first steps
- **[Quick Start Examples](#quick-start-examples)** - Common usage patterns
- **[System Requirements](#system-requirements)** - Platform and runtime requirements

### API Documentation
- **[API Reference](api-reference.md)** - Complete API documentation
- **[Core Interfaces](#core-interfaces)** - ISearchEngine, IFileSystemProvider, ISearchIndex
- **[Models & DTOs](#models--dtos)** - Data structures and transfer objects

### Advanced Topics
- **[Performance Guide](#performance-guide)** - Optimization techniques and best practices
- **[Architecture Overview](#architecture-overview)** - System design and components
- **[Platform-Specific Features](#platform-specific-features)** - Windows, Unix implementations

### Development
- **[Roadmap](roadmap.md)** - Future plans and platform support timeline
- **[Contributing Guidelines](#contributing)** - How to contribute to the project
- **[Release Notes](#release-notes)** - Version history and changes

## 🚀 Quick Start Examples

### Basic File Search
```csharp
using FastFind;

// Create search engine
var searchEngine = FastFinder.CreateSearchEngine();

// Simple search
var results = await searchEngine.SearchAsync("*.cs");
Console.WriteLine($"Found {results.TotalMatches} C# files");
```

### Advanced Search with Filters
```csharp
var query = new SearchQuery
{
    SearchText = "project",
    ExtensionFilter = ".cs",
    MinSize = 1024,
    MaxResults = 100
};

var results = await searchEngine.SearchAsync(query);
```

### Real-time Search
```csharp
await foreach (var result in searchEngine.SearchRealTimeAsync(query))
{
    Console.WriteLine($"Updated: {result.TotalMatches} matches");
}
```

## 💻 System Requirements

### Minimum Requirements
- **.NET Runtime**: .NET 9.0 or later
- **Memory**: 512 MB RAM available
- **Storage**: 100 MB free space for indexing cache
- **CPU**: x64 architecture (ARM64 planned for v2.0)

### Supported Platforms

| Platform | Version | Status | Package |
|----------|---------|--------|---------|
| Windows 10 | 1809+ | ✅ Stable | FastFind.Windows |
| Windows 11 | All | ✅ Stable | FastFind.Windows |
| Windows Server 2019 | All | ✅ Stable | FastFind.Windows |
| Windows Server 2022 | All | ✅ Stable | FastFind.Windows |
| Ubuntu Linux | 20.04+ | 🚧 Roadmap | FastFind.Unix |
| RHEL/CentOS | 8+ | 🚧 Roadmap | FastFind.Unix |
| macOS | 11+ | 🚧 Roadmap | FastFind.Unix |

## 🏗️ Architecture Overview

### Core Components

#### FastFind.Core
- **Interfaces**: Abstract contracts for search engines and file system providers
- **Models**: Data structures optimized for performance and memory efficiency
- **Utilities**: SIMD string matching, memory pools, caching systems

#### FastFind.Windows
- **WindowsSearchEngine**: High-performance Windows implementation
- **WindowsFileSystemProvider**: NTFS-optimized file enumeration
- **WindowsSearchIndex**: Memory-efficient indexing with real-time updates

#### FastFind.Unix (🚧 Planned)
- **UnixFileSystemProvider**: Linux/macOS file system access
- **Platform-specific optimizations**: ext4, APFS, FSEvents, inotify

### Data Flow
```
User Query → ISearchEngine → ISearchIndex → IFileSystemProvider → File System
                    ↓
            SearchResult ← Memory Pool ← SIMD Matching ← Cached Data
```

## ⚡ Performance Guide

### Memory Optimization
- **Use FastFileItem** for memory-critical scenarios (40% less memory)
- **Configure StringPool** for large datasets (string interning)
- **Monitor memory usage** with built-in statistics

### Search Performance
- **Index frequently accessed locations** for faster subsequent searches
- **Use specific search patterns** instead of broad wildcards
- **Leverage caching** with repeated similar queries

### Best Practices
```csharp
// Good: Specific search with filters
var query = new SearchQuery 
{ 
    SearchText = "config", 
    ExtensionFilter = ".json",
    MaxResults = 100 
};

// Avoid: Broad search without limits
var badQuery = new SearchQuery 
{ 
    SearchText = "*",  // Too broad
    MaxResults = int.MaxValue  // No limits
};
```

## 🔌 Core Interfaces

### ISearchEngine
Primary interface for all search operations, indexing, and monitoring.

**Key Methods:**
- `SearchAsync(SearchQuery query)` - Perform search with advanced filters
- `StartIndexingAsync(IndexingOptions options)` - Begin background indexing
- `GetSearchStatisticsAsync()` - Performance metrics and statistics

### IFileSystemProvider
Platform-specific file system access abstraction.

**Key Methods:**
- `EnumerateFilesAsync(locations, options)` - High-performance file enumeration
- `MonitorChangesAsync(locations, options)` - Real-time file system monitoring
- `GetAvailableLocationsAsync()` - Discover available drives/mount points

### ISearchIndex
Search index management and query execution.

**Key Methods:**
- `SearchAsync(SearchQuery query)` - Execute search against index
- `AddFilesAsync(files)` - Add files to search index
- `OptimizeAsync()` - Optimize index for better performance

## 📊 Models & DTOs

### Core Data Models

#### FileItem
Standard file information with computed properties.
```csharp
public class FileItem
{
    public string FullPath { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedTime { get; set; }
    // ... additional properties
}
```

#### FastFileItem
Memory-optimized struct using string interning (40% memory reduction).
```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct FastFileItem
{
    // Interned string IDs instead of full strings
    public readonly int FullPathId;
    public readonly int NameId;
    // ... optimized structure
}
```

#### SearchQuery
Comprehensive search parameters and filters.
```csharp
public class SearchQuery
{
    public string SearchText { get; set; }
    public bool UseRegex { get; set; }
    public string? ExtensionFilter { get; set; }
    public long? MinSize { get; set; }
    public int MaxResults { get; set; }
    // ... additional filters
}
```

## 🌐 Platform-Specific Features

### Windows Implementation
- **NTFS MFT Access**: Direct master file table reading for maximum speed
- **Junction Link Support**: Handle NTFS junctions and symbolic links
- **Volume Shadow Copy**: Integration with VSS for consistent snapshots
- **Windows Search Integration**: Optional Windows Search service integration

### Unix Implementation (🚧 Planned)
- **ext4 Optimization**: Native Linux file system optimizations
- **APFS Support**: macOS Advanced Portable File System support
- **inotify Integration**: Linux real-time file system monitoring
- **FSEvents Support**: macOS file system event monitoring

## 📈 Release Notes

### v1.0.0 (Current)
- ✅ Initial stable release
- ✅ Windows implementation with NTFS optimization
- ✅ Core interfaces and memory-optimized models
- ✅ SIMD-accelerated string matching
- ✅ Comprehensive API documentation

### Planned Releases
- **v1.1.0 (Q1 2025)**: Enhanced performance and monitoring
- **v1.2.0 (Q2 2025)**: Unix/Linux implementation
- **v2.0.0 (Q4 2025)**: Cloud-native and AI integration

See [Roadmap](roadmap.md) for detailed future plans.

## 🤝 Contributing

### How to Contribute
1. **Report Issues**: Use GitHub Issues for bug reports and feature requests
2. **Documentation**: Help improve documentation and examples
3. **Code**: Submit pull requests for bug fixes (major features require discussion)
4. **Testing**: Help test on different platforms and configurations

### Development Setup
```bash
# Clone repository
git clone https://github.com/iyulab/FastFind.NET.git
cd FastFind.NET

# Restore packages and build
dotnet restore
dotnet build

# Run tests (when available)
dotnet test
```

### Contribution Guidelines
- Follow existing code style and conventions
- Add tests for new functionality
- Update documentation for API changes
- Ensure cross-platform compatibility considerations

---

**💡 Need Help?**
- Check the [Getting Started Guide](getting-started.md)
- Review [API Reference](api-reference.md)
- Open an issue on GitHub
- Join discussions in GitHub Discussions