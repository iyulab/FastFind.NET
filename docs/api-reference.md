# FastFind.NET API Reference

## Core Interfaces

### ISearchEngine
Primary interface for file search operations with enhanced async support.

```csharp
public interface ISearchEngine : IAsyncDisposable, IDisposable
{
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SearchResult> SearchRealTimeAsync(SearchQuery query, CancellationToken cancellationToken = default);
    ValueTask DisposeAsync();
}
```

### FastFileItem
Ultra-optimized 61-byte struct with SIMD acceleration.

```csharp
public readonly struct FastFileItem
{
    public bool MatchesName(ReadOnlySpan<char> searchTerm);
    public string FullPath => StringPool.GetString(FullPathId);
}
```

## Usage Examples

```csharp
// Basic streaming search
WindowsRegistration.EnsureRegistered();
using var searchEngine = FastFinder.CreateWindowsSearchEngine(logger);

var results = await searchEngine.SearchAsync(query);
await foreach (var file in results.Files.ConfigureAwait(false))
{
    Console.WriteLine($"{file.Name} - {file.Size:N0} bytes");
}
```
