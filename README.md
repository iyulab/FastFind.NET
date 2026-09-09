# FastFind.NET

Ultra-high performance cross-platform file search library for .NET 10

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![Build Status](https://github.com/iyulab/FastFind.NET/actions/workflows/dotnet.yml/badge.svg)](https://github.com/iyulab/FastFind.NET/actions/workflows/dotnet.yml)

## Packages

| Package | Version | Description |
|---------|---------|-------------|
| **FastFind.Core** | [![NuGet](https://img.shields.io/nuget/v/FastFind.Core.svg)](https://www.nuget.org/packages/FastFind.Core) | Core interfaces, SIMD string matching, StringPool |
| **FastFind.Windows** | [![NuGet](https://img.shields.io/nuget/v/FastFind.Windows.svg)](https://www.nuget.org/packages/FastFind.Windows) | NTFS MFT direct access, USN Journal sync |
| **FastFind.Unix** | [![NuGet](https://img.shields.io/nuget/v/FastFind.Unix.svg)](https://www.nuget.org/packages/FastFind.Unix) | Linux/macOS parallel enumeration, file monitoring |
| **FastFind.SQLite** | [![NuGet](https://img.shields.io/nuget/v/FastFind.SQLite.svg)](https://www.nuget.org/packages/FastFind.SQLite) | Disk-backed persistent index |

## Key Features

- **Cross-Platform SIMD**: Vector256/Vector128 auto-dispatch (AVX2, SSE2, NEON) — 1.87M ops/sec
- **MFT Direct Access** (Windows): 31K+ files/sec NTFS enumeration, 30x faster than standard APIs
- **Parallel BFS Enumeration** (Linux/macOS): Channel-based depth-aware parallel traversal
- **Real-Time Monitoring**: USN Journal (Windows) / inotify (Linux) / FSEvents (macOS)
- **Disk-Backed Index**: Keep the index in SQLite instead of memory — footprint stays flat as the corpus grows
- **Memory Optimized**: paths and names are interned, so a repeated directory prefix is stored once
- **Auto Platform Detection**: ModuleInitializer auto-registration

## Installation

```bash
dotnet add package FastFind.Core

# Platform-specific (auto-registered at runtime)
dotnet add package FastFind.Windows    # Windows
dotnet add package FastFind.Unix       # Linux / macOS

dotnet add package FastFind.SQLite     # Optional: persistent index
```

## Quick Start

```csharp
using FastFind;

// Platform auto-detected — creates Windows, Linux, or macOS engine
using var engine = FastFinder.CreateSearchEngine();

await engine.StartIndexingAsync(new IndexingOptions
{
    SpecificDirectories = [@"D:\Projects"],         // Windows
    // MountPoints = ["/home", "/opt"],             // Linux / macOS
    ExcludedPaths = ["node_modules", ".git", "bin", "obj"],
    CollectFileSize = true
});

while (engine.IsIndexing) await Task.Delay(500);

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

### Keeping the index on disk

By default the index lives in memory. Give the engine a persistence store and it answers queries
from disk instead, so its footprint does not grow with the number of indexed files:

```csharp
using FastFind;
using FastFind.SQLite;

await using var store = SqlitePersistence.Create(@"D:\cache\index.db");
await store.InitializeAsync();

using var engine = FastFinder.CreateSearchEngine(store);

// Index once; the store survives the process.
await engine.StartIndexingAsync(new IndexingOptions { SpecificDirectories = [@"D:\Projects"] });
while (engine.IsIndexing) await Task.Delay(500);

Console.WriteLine($"{engine.Index!.Count} files indexed, {engine.Index.MemoryUsage} bytes held in memory");
```

Pass `PersistenceMode.MirrorInMemory` instead to keep the in-memory index for speed and use the store
purely for durability — that costs the memory of both, and is the right choice only when the corpus
comfortably fits.

The engine does not dispose a store you created; you keep ownership.

A store is safe to use from several threads or tasks at once — searching while indexing needs no
coordination from the caller, because each operation takes its own pooled connection. See
[the concurrency notes](docs/api-reference.md#concurrent-use) for what a transaction covers and the
one shape that is not supported.


## Performance

| Metric | Windows | Linux | macOS |
|--------|---------|-------|-------|
| SIMD String Matching | 1.87M ops/sec | 1.87M ops/sec | 1.87M ops/sec |
| File Enumeration | 31K files/sec (MFT) | Channel BFS parallel | Channel BFS parallel |
| Search Operations | 1.68M ops/sec | 1.68M ops/sec | 1.68M ops/sec |
| Memory per Op | 439 bytes | 439 bytes | 439 bytes |

## Platform Support

| Platform | Status | Package |
|----------|--------|---------|
| Windows 10/11, Server 2019+ | Production | FastFind.Windows |
| Linux (Ubuntu, RHEL, Alpine) | Preview | FastFind.Unix |
| macOS (Ventura+) | Preview | FastFind.Unix |

## Documentation

- [Getting Started](docs/getting-started.md) — Setup, configuration, examples
- [API Reference](docs/api-reference.md) — Interface and class signatures
- [Performance Benchmarks](docs/BENCHMARKS.md) — Detailed benchmark data
- [Roadmap](docs/roadmap.md) — Development plans

## License

MIT License - see [LICENSE](LICENSE) for details.
