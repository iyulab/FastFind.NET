# FastFind.NET Search Performance Optimization Roadmap

## Executive Summary

Based on comprehensive benchmark analysis, FastFind.NET shows excellent performance for indexed queries (30μs) but significant bottlenecks for path-based searches (150-190ms). This roadmap addresses identified deficiencies to achieve Everything-level search performance.

### Current Performance Baseline

| Query Type | Current | Target | Improvement |
|------------|---------|--------|-------------|
| Search_WithSizeFilter | 30μs | 30μs | ✅ Optimal |
| Search_SimpleText | 1.5ms | 500μs | 3x |
| Search_FileNameOnly | 154ms | 10ms | 15x |
| Search_WithBasePath | 189ms | 5ms | 38x |

---

## Identified Bottlenecks

### 1. Unconditional Filesystem Fallback (Critical)
**Location**: `WindowsSearchEngine.ShouldPerformFilesystemFallback()`

```csharp
// Current behavior - ALWAYS returns true for BasePath queries
private bool ShouldPerformFilesystemFallback(SearchQuery query)
{
    if (!string.IsNullOrEmpty(query.BasePath))
        return true;  // ❌ Forces filesystem scan even when index has data
    // ...
}
```

**Impact**: Path-based queries bypass index entirely, triggering full filesystem traversal.

### 2. O(n) Full Index Scan
**Location**: `WindowsSearchEngine.GetSearchCandidatesSync()`

```csharp
// Current behavior - scans ALL files in index
private IEnumerable<FastFileItem> GetSearchCandidatesSync(SearchQuery query)
{
    _indexLock.EnterReadLock();
    try
    {
        foreach (var item in _fileIndex.Values)  // ❌ O(n) scan
        {
            if (MatchesQuery(item, query))
                yield return item;
        }
    }
    finally { _indexLock.ExitReadLock(); }
}
```

**Impact**: Every search iterates through entire index regardless of query specificity.

### 3. No Path Prefix Index
**Current State**: Only `Dictionary<string, FastFileItem>` by full path.
**Missing**: Trie or prefix-based index for hierarchical path lookups.

**Impact**: Cannot efficiently query "all files under C:\Windows" without full scan.

### 4. String Operations in Hot Path
**Location**: `WindowsSearchEngine.MatchesQuery()`

```csharp
// Current behavior - string allocations per file
private bool MatchesQuery(FastFileItem item, SearchQuery query)
{
    var name = StringPool.GetString(item.NameId);
    var ext = StringPool.GetString(item.ExtensionId);

    if (!string.IsNullOrEmpty(query.SearchPattern))
    {
        var pattern = query.SearchPattern.ToLowerInvariant();  // ❌ Allocation
        var nameLower = name.ToLowerInvariant();  // ❌ Allocation
        // ...
    }
}
```

**Impact**: Memory allocations and string operations per file during search.

### 5. SIMD Underutilization
**Current State**: `SIMDStringMatcher` exists with 80M+ ops/sec capability but is NOT used in the main search path.

**Impact**: Missing 10-100x performance gains from vectorized string matching.

---

## Phase 1: Index Architecture Optimization

### 1.1 Path Prefix Trie Index

**Goal**: O(log n) path-based lookups instead of O(n) scans.

**Implementation**:
```csharp
public sealed class PathTrieIndex
{
    private readonly TrieNode _root = new();

    public void AddPath(int pathId, FastFileItem item)
    {
        var path = StringPool.GetString(pathId);
        var segments = path.Split(Path.DirectorySeparatorChar);
        var node = _root;

        foreach (var segment in segments)
        {
            node = node.GetOrAddChild(segment);
        }
        node.Items.Add(item);
    }

    public IEnumerable<FastFileItem> GetItemsUnderPath(string basePath)
    {
        var segments = basePath.Split(Path.DirectorySeparatorChar);
        var node = _root;

        foreach (var segment in segments)
        {
            if (!node.TryGetChild(segment, out node))
                return Enumerable.Empty<FastFileItem>();
        }

        return node.GetAllDescendantItems();  // O(k) where k = items under path
    }
}
```

**Files to Modify**:
- `src/FastFind/Models/PathTrieIndex.cs` (NEW)
- `src/FastFind.Windows/WindowsSearchEngine.cs`
- `src/FastFind/Interfaces/ISearchIndex.cs`

**Expected Improvement**: Search_WithBasePath 189ms → 5ms (38x)

### 1.2 Smart Filesystem Fallback

**Goal**: Only perform filesystem fallback when necessary.

**Implementation**:
```csharp
private bool ShouldPerformFilesystemFallback(SearchQuery query)
{
    // Check if index covers the requested path
    if (!string.IsNullOrEmpty(query.BasePath))
    {
        // Only fallback if path is NOT in index
        if (_pathTrieIndex.ContainsPath(query.BasePath))
            return false;  // ✅ Use index

        // Only fallback if index is stale (>5 minutes old)
        if (_lastIndexTime.AddMinutes(5) > DateTime.UtcNow)
            return false;  // ✅ Use recent index
    }

    return base.ShouldPerformFilesystemFallback(query);
}
```

**Files to Modify**:
- `src/FastFind.Windows/WindowsSearchEngine.cs`

**Expected Improvement**: Eliminates unnecessary filesystem traversals.

### 1.3 Extension Index

**Goal**: Fast lookup by file extension.

**Implementation**:
```csharp
private readonly ConcurrentDictionary<int, ConcurrentBag<FastFileItem>> _extensionIndex = new();

// O(1) lookup for ".cs" files
public IEnumerable<FastFileItem> GetItemsByExtension(int extensionId)
{
    return _extensionIndex.TryGetValue(extensionId, out var items)
        ? items
        : Enumerable.Empty<FastFileItem>();
}
```

**Expected Improvement**: Extension-filtered searches 100x faster.

---

## Phase 2: SIMD Integration in Search Path

### 2.1 Batch String Matching

**Goal**: Use SIMD for bulk pattern matching.

**Implementation**:
```csharp
public sealed class SIMDBatchMatcher
{
    private readonly SIMDStringMatcher _matcher;

    public ReadOnlySpan<int> MatchBatch(
        ReadOnlySpan<int> nameIds,
        ReadOnlySpan<char> pattern)
    {
        Span<int> results = stackalloc int[nameIds.Length];
        int matchCount = 0;

        // Process 8 names at a time with AVX2
        for (int i = 0; i < nameIds.Length; i += 8)
        {
            var batch = nameIds.Slice(i, Math.Min(8, nameIds.Length - i));
            matchCount += _matcher.MatchBatchAVX2(batch, pattern, results.Slice(matchCount));
        }

        return results.Slice(0, matchCount);
    }
}
```

**Files to Create**:
- `src/FastFind/Implementation/SIMDBatchMatcher.cs`

**Files to Modify**:
- `src/FastFind.Windows/WindowsSearchEngine.cs`

**Expected Improvement**: Search_FileNameOnly 154ms → 10ms (15x)

### 2.2 Zero-Allocation Pattern Matching

**Goal**: Eliminate string allocations in hot path.

**Implementation**:
```csharp
private bool MatchesQueryOptimized(FastFileItem item, SearchQuery query)
{
    if (!string.IsNullOrEmpty(query.SearchPattern))
    {
        // Get span directly from StringPool without allocation
        var nameSpan = StringPool.GetSpan(item.NameId);
        var patternSpan = query.SearchPattern.AsSpan();

        // Use SIMD-accelerated case-insensitive contains
        if (!SIMDStringMatcher.ContainsIgnoreCase(nameSpan, patternSpan))
            return false;
    }
    // ...
}
```

**Files to Modify**:
- `src/FastFind/Models/StringPool.cs` - Add `GetSpan(int id)` method
- `src/FastFind.Windows/WindowsSearchEngine.cs`

**Expected Improvement**: 50% reduction in GC pressure during searches.

---

## Phase 3: Lock-Free Streaming Architecture

### 3.1 Lock-Free Index Structure

**Goal**: Eliminate ReaderWriterLockSlim contention.

**Implementation**:
```csharp
public sealed class LockFreeFileIndex
{
    // Immutable snapshots for reads
    private volatile ImmutableDictionary<string, FastFileItem> _snapshot;

    // Lock-free read
    public FastFileItem? TryGet(string path)
    {
        return _snapshot.TryGetValue(path, out var item) ? item : null;
    }

    // Copy-on-write update
    public void AddOrUpdate(string path, FastFileItem item)
    {
        ImmutableDictionary<string, FastFileItem> original, updated;
        do
        {
            original = _snapshot;
            updated = original.SetItem(path, item);
        } while (Interlocked.CompareExchange(ref _snapshot, updated, original) != original);
    }
}
```

**Files to Create**:
- `src/FastFind/Implementation/LockFreeFileIndex.cs`

**Expected Improvement**: Eliminates lock contention in parallel searches.

### 3.2 Channel-Based Result Streaming

**Goal**: Stream results as they're found instead of batching.

**Implementation**:
```csharp
public async IAsyncEnumerable<FastFileItem> SearchStreamingAsync(
    SearchQuery query,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    var channel = Channel.CreateUnbounded<FastFileItem>(
        new UnboundedChannelOptions { SingleReader = true });

    // Producer: search in background
    _ = Task.Run(async () =>
    {
        await foreach (var item in SearchInternalAsync(query, ct))
        {
            await channel.Writer.WriteAsync(item, ct);
        }
        channel.Writer.Complete();
    }, ct);

    // Consumer: yield as results arrive
    await foreach (var item in channel.Reader.ReadAllAsync(ct))
    {
        yield return item;
    }
}
```

**Files to Modify**:
- `src/FastFind.Windows/WindowsSearchEngine.cs`

**Expected Improvement**: First result latency reduced from seconds to milliseconds.

---

## Phase 4: Query Optimizer

### 4.1 Query Plan Generation

**Goal**: Choose optimal execution strategy based on query characteristics.

**Implementation**:
```csharp
public sealed class QueryOptimizer
{
    public QueryPlan CreatePlan(SearchQuery query, IndexStatistics stats)
    {
        var plan = new QueryPlan();

        // Use extension index if filtering by extension
        if (!string.IsNullOrEmpty(query.Extension))
        {
            plan.UseExtensionIndex = true;
            plan.EstimatedCost = stats.FilesWithExtension(query.Extension);
        }

        // Use path trie if BasePath specified
        if (!string.IsNullOrEmpty(query.BasePath))
        {
            plan.UsePathTrie = true;
            plan.EstimatedCost = stats.FilesUnderPath(query.BasePath);
        }

        // Use SIMD batch matching for text patterns
        if (!string.IsNullOrEmpty(query.SearchPattern) && plan.EstimatedCost > 1000)
        {
            plan.UseSIMDBatch = true;
        }

        return plan;
    }
}
```

**Files to Create**:
- `src/FastFind/Implementation/QueryOptimizer.cs`
- `src/FastFind/Models/QueryPlan.cs`

**Expected Improvement**: Automatic selection of fastest execution path.

---

## Implementation Schedule

### Phase 1 (Foundation) - Priority: Critical
| Task | Complexity | Impact | Status |
|------|------------|--------|--------|
| 1.1 Path Prefix Trie | Medium | High | ✅ Completed |
| 1.2 Smart Filesystem Fallback | Low | High | ✅ Completed |
| 1.3 Extension Index | Low | Medium | ✅ Already Existed |

### Phase 2 (SIMD) - Priority: High
| Task | Complexity | Impact | Status |
|------|------------|--------|--------|
| 2.1 SIMD String Matching | High | High | ✅ Completed |
| 2.2 Zero-Allocation Matching | Medium | Medium | 🔴 Pending |

### Phase 3 (Architecture) - Priority: Medium
| Task | Complexity | Impact |
|------|------------|--------|
| 3.1 Lock-Free Index | High | Medium |
| 3.2 Streaming Results | Medium | Medium |

### Phase 4 (Intelligence) - Priority: Low
| Task | Complexity | Impact |
|------|------------|--------|
| 4.1 Query Optimizer | High | High |

---

## Verification Tests

### Performance Benchmarks
```csharp
[Benchmark]
public async Task Search_WithBasePath_Optimized()
{
    var query = new SearchQuery { BasePath = @"C:\Windows\System32" };
    var result = await _engine.SearchAsync(query);
    await foreach (var _ in result.Files) { }
}

// Target: < 5ms for 10,000 files under path
```

### Unit Tests
```csharp
[Fact]
public void PathTrieIndex_GetItemsUnderPath_ReturnsOnlyDescendants()
{
    var trie = new PathTrieIndex();
    trie.AddPath(@"C:\Windows\System32\cmd.exe", item1);
    trie.AddPath(@"C:\Windows\notepad.exe", item2);
    trie.AddPath(@"C:\Program Files\app.exe", item3);

    var results = trie.GetItemsUnderPath(@"C:\Windows").ToList();

    results.Should().HaveCount(2);
    results.Should().Contain(item1);
    results.Should().Contain(item2);
    results.Should().NotContain(item3);
}
```

### Integration Tests
```csharp
[Fact]
public async Task SearchAsync_WithBasePath_UsesIndexNotFilesystem()
{
    // Arrange
    await _engine.BuildIndexAsync(@"C:\TestData");

    // Act
    var query = new SearchQuery { BasePath = @"C:\TestData\SubFolder" };
    var result = await _engine.SearchAsync(query);

    // Assert
    result.Statistics.IndexHits.Should().BeGreaterThan(0);
    result.Statistics.FilesystemReads.Should().Be(0);  // No fallback!
}
```

---

## Success Criteria

| Metric | Current | Target | Status |
|--------|---------|--------|--------|
| Search_WithBasePath | 189ms → TBD | <5ms | 🟡 Phase 1.1/1.2 implemented |
| Search_FileNameOnly | 154ms → TBD | <10ms | 🟡 Phase 2.1 SIMD integrated |
| Memory per 1M files | ~500MB | <200MB | 🟡 Phase 2.2 pending |
| First result latency | ~1s | <100ms | 🔴 Phase 3 needed |
| Index rebuild time | N/A | <30s/1M files | 🟡 |

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Trie memory overhead | Medium | Lazy node allocation, pruning |
| Lock-free complexity | High | Extensive testing, fallback option |
| SIMD portability | Medium | Runtime feature detection |
| Breaking API changes | Low | Maintain backward compatibility |

---

## Next Steps

1. ~~**Immediate**: Implement Phase 1.2 (Smart Filesystem Fallback)~~ ✅ Completed
2. ~~**Short-term**: Implement Phase 1.1 (Path Trie Index)~~ ✅ Completed
3. ~~**Phase 1.3**: Extension Index~~ ✅ Already existed in codebase
4. ~~**Phase 2.1**: SIMD String Matching~~ ✅ Completed
5. **Next**: Implement Phase 2.2 (Zero-Allocation Matching)
6. **Medium-term**: Implement Phase 3 (Lock-Free Architecture)
7. **Long-term**: Implement Phase 4 (Query Optimizer)

---

## Related Documentation

- [MFT Optimization Roadmap](./MFT_OPTIMIZATION_ROADMAP.md)
- [Architecture Overview](./ARCHITECTURE.md)
- [Performance Benchmarks](../src/FastFind.Benchmarks/)

---

*Last Updated: 2026-01-13*
*Author: FastFind.NET Development Team*
