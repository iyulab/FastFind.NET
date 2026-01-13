# MFT Optimization Implementation Roadmap

## Project Philosophy Alignment

| Principle | Application |
|-----------|-------------|
| **Ultra-high Performance** | Target 200K+ records/sec (40% of Everything) |
| **Existing Components** | Leverage StringPool, SIMDStringMatcher |
| **Span-based Operations** | BinaryPrimitives, zero-allocation parsing |
| **.NET 10 Optimizations** | SearchValues, IAsyncEnumerable |
| **Test-First** | Tests before implementation, CI/CD safe |

---

## Phase Overview

| Phase | Focus | Target Improvement | Duration |
|-------|-------|-------------------|----------|
| **1.1** | Span-based Parsing | +25% speed | 1 day |
| **1.2** | Buffer Optimization | +15% speed | 1 day |
| **1.3** | StringPool Integration | -50% memory | 1 day |
| **1.4** | MftCompactRecord | -30% memory | 1 day |
| **2.x** | I/O & Concurrency | +20% speed | Future |

---

## Phase 1.1: Span-based Binary Parsing

### Objective
Replace `BitConverter.ToXXX()` with `BinaryPrimitives` and `Span<T>` for zero-allocation parsing.

### Current Code (MftReader.cs:435-490)
```csharp
// BEFORE: Allocation-heavy
recordLength = BitConverter.ToUInt32(buffer, offset);
fileReferenceNumber = BitConverter.ToUInt64(buffer, offset + 8);
var fileName = Encoding.Unicode.GetString(buffer, offset + fileNameOffset, fileNameLength);
```

### Target Code
```csharp
// AFTER: Zero-allocation
var span = buffer.AsSpan(offset);
recordLength = BinaryPrimitives.ReadUInt32LittleEndian(span);
fileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
var fileNameSpan = MemoryMarshal.Cast<byte, char>(span.Slice(fileNameOffset, fileNameLength));
```

### Tasks

#### Task 1.1.1: Create MftParserV2 with Span-based Parsing
**File:** `src/FastFind.Windows/Mft/MftParserV2.cs`

```csharp
public static class MftParserV2
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParseUsnRecord(
        ReadOnlySpan<byte> buffer,
        ref int offset,
        out MftFileRecord record);
}
```

**Acceptance Criteria:**
- [ ] No heap allocations in hot path (except filename string)
- [ ] Uses BinaryPrimitives for all integer reads
- [ ] AggressiveInlining on all methods
- [ ] Handles malformed records gracefully

#### Task 1.1.2: Create Parsing Performance Tests
**File:** `src/FastFind.Windows.Tests/Mft/MftParserPerformanceTests.cs`

```csharp
[Trait("Category", "Performance")]
[Trait("Suite", "MFT")]
public class MftParserPerformanceTests
{
    // Baseline: Current implementation
    [Fact]
    public void ParseUsnRecord_BitConverter_Baseline();

    // Target: New Span-based implementation
    [Fact]
    public void ParseUsnRecord_Span_MustBe25PercentFaster();

    // Memory: Zero allocations in parsing
    [Fact]
    public void ParseUsnRecord_Span_ZeroAllocations();
}
```

**Test Specification:**
| Test | Metric | Target |
|------|--------|--------|
| Speed comparison | ops/sec | +25% vs baseline |
| Memory allocation | bytes/op | 0 (except filename) |
| Correctness | parsed fields | 100% match |

#### Task 1.1.3: Integration Test
**File:** `src/FastFind.Windows.Tests/Mft/MftParserIntegrationTests.cs`

```csharp
public class MftParserIntegrationTests
{
    [Fact]
    public async Task ParseRealMftData_V2Parser_MatchesV1Output();

    [Fact]
    public async Task ParseRealMftData_V2Parser_HandlesAllRecordTypes();
}
```

### Commit Strategy
```
1. test: add MftParserPerformanceTests with baseline measurements
2. feat: implement MftParserV2 with Span-based parsing
3. test: verify 25% performance improvement
4. refactor: integrate MftParserV2 into MftReader
```

---

## Phase 1.2: Buffer Size Optimization

### Objective
Increase buffer size from 64KB to optimal size based on benchmarks.

### Research Findings
- [OSR Community](https://community.osr.com/t/deviceiocontrol-buffer-size-limit/12797): 1MB buffers work since early NT
- [xplorer² blog](https://www.zabkat.com/blog/buffered-disk-access.htm): 32KB-4MB optimal range
- Current: 64KB → Target: Test 256KB, 1MB, 4MB

### Tasks

#### Task 1.2.1: Buffer Size Benchmark Tests
**File:** `src/FastFind.Windows.Tests/Mft/MftBufferSizeTests.cs`

```csharp
[Trait("Category", "Performance")]
public class MftBufferSizeTests
{
    [Theory]
    [InlineData(64 * 1024)]      // Current: 64KB
    [InlineData(256 * 1024)]     // Test: 256KB
    [InlineData(1024 * 1024)]    // Test: 1MB
    [InlineData(4 * 1024 * 1024)] // Test: 4MB
    public async Task MftEnumeration_BufferSize_MeasureThroughput(int bufferSize);

    [Fact]
    public void DetermineOptimalBufferSize_ForCurrentSystem();
}
```

#### Task 1.2.2: Configurable Buffer Size
**File:** `src/FastFind.Windows/Mft/MftReaderOptions.cs`

```csharp
public sealed class MftReaderOptions
{
    public int BufferSize { get; init; } = 1024 * 1024; // 1MB default
    public bool UseStringPooling { get; init; } = true;
    public StringPool? SharedStringPool { get; init; }
}
```

### Commit Strategy
```
1. test: add buffer size benchmark tests
2. feat: add MftReaderOptions for configurable buffer
3. perf: set optimal buffer size based on benchmarks
```

---

## Phase 1.3: StringPool Integration

### Objective
Integrate existing StringPool for filename interning to reduce memory usage.

### Existing StringPool API (src/FastFind/Models/StringPool.cs)
```csharp
public class StringPool
{
    public uint Intern(string value);           // Returns ID
    public string? GetString(uint id);          // Returns string
    public uint InternName(string name);        // Optimized for filenames
    public uint InternExtension(string ext);    // Optimized for extensions
}
```

### Tasks

#### Task 1.3.1: Add Span-based Intern Method to StringPool
**File:** `src/FastFind/Models/StringPool.cs`

```csharp
// NEW: Zero-allocation intern from char span
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public uint InternFromSpan(ReadOnlySpan<char> value)
{
    // Fast path: check if already interned
    var hash = string.GetHashCode(value);
    if (TryGetExisting(hash, value, out var id))
        return id;

    // Slow path: create string and intern
    return Intern(new string(value));
}
```

#### Task 1.3.2: StringPool Span Tests
**File:** `src/FastFind.Windows.Tests/Core/StringPoolSpanTests.cs`

```csharp
public class StringPoolSpanTests
{
    [Fact]
    public void InternFromSpan_SameAsInternString();

    [Fact]
    public void InternFromSpan_CacheHit_ZeroAllocations();

    [Fact]
    public void InternFromSpan_Performance_NotSlowerThanString();
}
```

#### Task 1.3.3: Integrate StringPool into MftParserV2
```csharp
public static bool TryParseUsnRecord(
    ReadOnlySpan<byte> buffer,
    ref int offset,
    StringPool stringPool,  // NEW: StringPool parameter
    out MftFileRecord record)
{
    // ...
    var fileNameSpan = MemoryMarshal.Cast<byte, char>(span.Slice(fileNameOffset, fileNameLength));
    var fileNameId = stringPool.InternFromSpan(fileNameSpan);
    // ...
}
```

### Commit Strategy
```
1. test: add StringPool span-based tests
2. feat: add InternFromSpan method to StringPool
3. feat: integrate StringPool into MftParserV2
4. test: verify memory reduction with real MFT data
```

---

## Phase 1.4: MftCompactRecord Structure

### Objective
Create memory-optimized record structure using string IDs instead of string references.

### Current vs Target

| Field | MftFileRecord (Current) | MftCompactRecord (Target) |
|-------|------------------------|--------------------------|
| FileReferenceNumber | 8 bytes | 8 bytes |
| ParentFileReferenceNumber | 8 bytes | 8 bytes |
| FileNameId | 8 bytes (string ref) | 4 bytes (uint ID) |
| Attributes | 4 bytes | 4 bytes |
| FileSize | 8 bytes | 8 bytes |
| Timestamps | 24 bytes (3 DateTime) | 8 bytes (1 long ticks) |
| **Total** | **60+ bytes + string** | **40 bytes** |

### Tasks

#### Task 1.4.1: Define MftCompactRecord
**File:** `src/FastFind.Windows/Mft/MftCompactRecord.cs`

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct MftCompactRecord
{
    public readonly ulong FileReferenceNumber;       // 8
    public readonly ulong ParentFileReferenceNumber; // 8
    public readonly uint FileNameId;                 // 4 (StringPool ID)
    public readonly uint Attributes;                 // 4
    public readonly long FileSize;                   // 8
    public readonly long ModifiedTicks;              // 8 (DateTime.Ticks)
    // Total: 40 bytes

    // Helper methods
    public bool IsDirectory => (Attributes & 0x10) != 0;
    public DateTime ModifiedTime => new DateTime(ModifiedTicks, DateTimeKind.Utc);
}
```

#### Task 1.4.2: MftCompactRecord Tests
**File:** `src/FastFind.Windows.Tests/Mft/MftCompactRecordTests.cs`

```csharp
public class MftCompactRecordTests
{
    [Fact]
    public void MftCompactRecord_Size_Is40Bytes()
    {
        Marshal.SizeOf<MftCompactRecord>().Should().Be(40);
    }

    [Fact]
    public void MftCompactRecord_CanConvertFromMftFileRecord();

    [Fact]
    public void MftCompactRecord_WithStringPool_ResolvesFileName();
}
```

### Commit Strategy
```
1. test: add MftCompactRecord structure tests
2. feat: implement MftCompactRecord struct
3. feat: add conversion methods between record types
4. refactor: update MftReader to use MftCompactRecord internally
```

---

## Verification Matrix

### Phase 1.1 Verification
| Test | Command | Success Criteria |
|------|---------|------------------|
| Unit tests | `dotnet test --filter "MftParserPerformanceTests"` | All pass |
| Speed benchmark | Manual benchmark run | +25% vs baseline |
| Integration | `dotnet test --filter "MftParserIntegrationTests"` | All pass |

### Phase 1.2 Verification
| Test | Command | Success Criteria |
|------|---------|------------------|
| Buffer tests | `dotnet test --filter "MftBufferSizeTests"` | Optimal size determined |
| CI/CD | `dotnet build && dotnet test` | No regression |

### Phase 1.3 Verification
| Test | Command | Success Criteria |
|------|---------|------------------|
| StringPool tests | `dotnet test --filter "StringPoolSpanTests"` | All pass |
| Memory test | MftStrictPerformanceTests | &lt;150 bytes/record |

### Phase 1.4 Verification
| Test | Command | Success Criteria |
|------|---------|------------------|
| Structure tests | `dotnet test --filter "MftCompactRecordTests"` | 40 bytes confirmed |
| Full validation | `dotnet test --filter "MftStrictPerformanceTests"` | All criteria met |

---

## Final Phase 1 Success Criteria

| Metric | Before | After Phase 1 | Target |
|--------|--------|---------------|--------|
| Enumeration Rate | 144K/sec | 180K/sec | 200K/sec |
| Memory per Record | 209 bytes | 80 bytes | 100 bytes |
| Time per 100K | 694 ms | 550 ms | 500 ms |

---

## Current Status

### Completed
- [x] Phase 1 상세 태스크 도출
- [x] 검증 테스트 설계
- [x] 구현 로드맵 작성

### Next Steps
1. **Phase 1.1 시작**: MftParserV2 테스트 작성
2. **Baseline 측정**: 현재 파서 성능 기록
3. **Span 구현**: BinaryPrimitives 기반 파서
4. **검증**: 25% 성능 향상 확인

### Ready for Implementation
```bash
# Start Phase 1.1
dotnet test --filter "MftParserPerformanceTests" --list-tests
```
