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
| **FastFind.Unix** | [![NuGet](https://img.shields.io/nuget/v/FastFind.Unix.svg)](https://www.nuget.org/packages/FastFind.Unix) | Linux/macOS with parallel enumeration & inotify |
| **FastFind.SQLite** | [![NuGet](https://img.shields.io/nuget/v/FastFind.SQLite.svg)](https://www.nuget.org/packages/FastFind.SQLite) | SQLite persistence with FTS5 search |

## Key Features

- **Cross-Platform SIMD**: Vector256/Vector128 auto-dispatch (AVX2, SSE2, NEON) — 1.87M ops/sec
- **MFT Direct Access** (Windows): 31K+ files/sec NTFS enumeration (30x faster than standard APIs)
- **Parallel File Enumeration** (Linux): Channel-based BFS with depth-aware parallelism
- **USN Journal Sync** (Windows) / **inotify** (Linux): Real-time file change detection
- **SQLite FTS5**: Persistent index with full-text search
- **Memory Optimized**: 60-80% reduction through string interning
- **Auto Platform Detection**: ModuleInitializer auto-registration per platform

## Installation

```bash
dotnet add package FastFind.Core

# Platform-specific (auto-registered at runtime)
dotnet add package FastFind.Windows    # Windows
dotnet add package FastFind.Unix       # Linux / macOS

dotnet add package FastFind.SQLite     # Optional: SQLite persistence
```

## Quick Start

### Windows

```csharp
using FastFind;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var searchEngine = FastFinder.CreateWindowsSearchEngine(loggerFactory);

await searchEngine.StartIndexingAsync(new IndexingOptions
{
    DriveLetters = ['C', 'D'],
    ExcludedPaths = ["node_modules", "bin", "obj", ".git"],
    CollectFileSize = true
});

while (searchEngine.IsIndexing) await Task.Delay(500);

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

### Linux

```csharp
using FastFind;
using FastFind.Unix;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var searchEngine = UnixSearchEngine.CreateLinuxSearchEngine(loggerFactory);

await searchEngine.StartIndexingAsync(new IndexingOptions
{
    MountPoints = ["/home", "/opt"],
    ExcludedPaths = ["node_modules", ".git", "__pycache__"],
    CollectFileSize = true
});

while (searchEngine.IsIndexing) await Task.Delay(500);

var results = await searchEngine.SearchAsync(new SearchQuery
{
    BasePath = "/home/user/projects",
    SearchText = "config",
    ExtensionFilter = ".json",
    MaxResults = 100
});

await foreach (var file in results.Files)
{
    Console.WriteLine($"{file.Name} - {file.DirectoryPath}");
}
```

## Performance

| Metric | Windows | Linux |
|--------|---------|-------|
| SIMD String Matching | 1,877,459 ops/sec | 1,877,459 ops/sec (same Vector256) |
| File Enumeration | 31,073 files/sec (MFT) | Channel BFS parallel |
| File Indexing | 243,856 files/sec | Channel-based async |
| Search Operations | 1,680,631 ops/sec | 1,680,631 ops/sec |
| StringPool Interning | 6,437 paths/sec | 6,437 paths/sec |

See [Performance Benchmarks](docs/BENCHMARKS.md) for detailed results.

## Documentation

- [Getting Started](docs/getting-started.md)
- [API Reference](docs/api-reference.md)
- [Performance Benchmarks](docs/BENCHMARKS.md)
- [Search Examples](docs/search-examples.md)
- [Roadmap](docs/roadmap.md)

## Platform Support

| Platform | Status | Package |
|----------|--------|---------|
| Windows 10/11, Server 2019+ | Production Ready | FastFind.Windows |
| Linux (Ubuntu, RHEL, Alpine) | Preview | FastFind.Unix |
| macOS | Planned (Phase 2) | FastFind.Unix |

## License

MIT License - see [LICENSE](LICENSE) for details.
