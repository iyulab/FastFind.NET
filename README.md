# FastFind.NET

Ultra-high performance cross-platform file search library for .NET 10

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![Build Status](https://github.com/iyulab/FastFind.NET/actions/workflows/dotnet.yml/badge.svg)](https://github.com/iyulab/FastFind.NET/actions/workflows/dotnet.yml)

## Packages

| Package | Version | Description |
|---------|---------|-------------|
| **FastFind.Core** | [![NuGet](https://img.shields.io/nuget/v/FastFind.Core.svg)](https://www.nuget.org/packages/FastFind.Core) | Core interfaces and models |
| **FastFind.Windows** | [![NuGet](https://img.shields.io/nuget/v/FastFind.Windows.svg)](https://www.nuget.org/packages/FastFind.Windows) | Windows-optimized with MFT & USN Journal |
| **FastFind.SQLite** | [![NuGet](https://img.shields.io/nuget/v/FastFind.SQLite.svg)](https://www.nuget.org/packages/FastFind.SQLite) | SQLite persistence with FTS5 search |

## Key Features

- **SIMD-Accelerated Search**: 1.87M ops/sec with AVX2 hardware acceleration
- **MFT Direct Access**: 31K+ files/sec NTFS enumeration (30x faster than standard APIs)
- **USN Journal Sync**: Real-time file change detection
- **SQLite FTS5**: Persistent index with full-text search
- **Memory Optimized**: 60-80% reduction through string interning
- **Optional File Size**: Opt-in file size collection with ~10-30% overhead

## Installation

```bash
dotnet add package FastFind.Core
dotnet add package FastFind.Windows    # Windows implementation
dotnet add package FastFind.SQLite     # Optional: SQLite persistence
```

## Quick Start

```csharp
using FastFind;
using Microsoft.Extensions.Logging;

// Create search engine
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var searchEngine = FastFinder.CreateWindowsSearchEngine(loggerFactory);

// Build index first (required before searching)
await searchEngine.StartIndexingAsync(new IndexingOptions
{
    DriveLetters = ['C', 'D'],
    ExcludedPaths = ["node_modules", "bin", "obj", ".git"],
    CollectFileSize = true  // Enable file size collection (default: false for max speed)
});

// Wait for indexing
while (searchEngine.IsIndexing)
{
    await Task.Delay(500);
}

// Search
var results = await searchEngine.SearchAsync(new SearchQuery
{
    BasePath = @"D:\Projects",
    SearchText = "controller",
    ExtensionFilter = ".cs",
    MaxResults = 100
});

await foreach (var file in results.Files)
{
    Console.WriteLine($"{file.Name} ({file.SizeFormatted})");
}
```

## Performance

| Metric | Result |
|--------|--------|
| SIMD String Matching | 1,877,459 ops/sec |
| MFT File Enumeration | 31,073 files/sec |
| File Indexing | 243,856 files/sec |
| Search Operations | 1,680,631 ops/sec |
| StringPool Interning | 6,437 paths/sec |

See [Performance Benchmarks](docs/BENCHMARKS.md) for detailed results.

## Documentation

- [Getting Started](docs/getting-started.md)
- [API Reference](docs/api-reference.md)
- [Performance Benchmarks](docs/BENCHMARKS.md)
- [Search Examples](docs/search-examples.md)
- [Roadmap](docs/roadmap.md)

## Platform Support

| Platform | Status |
|----------|--------|
| Windows 10/11, Server 2019+ | Production Ready |
| Linux/macOS | Planned (Q2 2026) |

## License

MIT License - see [LICENSE](LICENSE) for details.
