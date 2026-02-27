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
| **FastFind.SQLite** | [![NuGet](https://img.shields.io/nuget/v/FastFind.SQLite.svg)](https://www.nuget.org/packages/FastFind.SQLite) | FTS5 persistent index |

## Key Features

- **Cross-Platform SIMD**: Vector256/Vector128 auto-dispatch (AVX2, SSE2, NEON) — 1.87M ops/sec
- **MFT Direct Access** (Windows): 31K+ files/sec NTFS enumeration, 30x faster than standard APIs
- **Parallel BFS Enumeration** (Linux/macOS): Channel-based depth-aware parallel traversal
- **Real-Time Monitoring**: USN Journal (Windows) / inotify (Linux) / FSEvents (macOS)
- **SQLite FTS5**: Persistent full-text search index
- **Memory Optimized**: 60-80% reduction via StringPool interning
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
