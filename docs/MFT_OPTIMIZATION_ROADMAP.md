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
- [x] Phase 1.1 MftParserV2 구현
- [x] Phase 1.1 성능 테스트 실행 및 분석

### Phase 1.1 Research Findings (2026-01-13)

**가설**: Span/BinaryPrimitives가 BitConverter보다 25%+ 빠를 것

**실험 결과**:
| 구현 | 성능 (records/sec) | 상대 속도 |
|------|-------------------|----------|
| BitConverter | 7,762,294 | 1.00x (baseline) |
| Span/BinaryPrimitives | 4,858,558 | 0.63x (**37% 느림**) |

**분석**:
1. 현대 little-endian x64 시스템에서 BitConverter는 이미 고도로 최적화됨
2. Span slicing 연산이 추가 오버헤드 발생
3. MemoryMarshal.Cast<byte, char> 변환에서 추가 비용
4. 정수 파싱은 이미 병목이 아님 - I/O와 문자열 할당이 주요 병목

**결론**: Phase 1.1 (Span-based parsing) 가설 반증됨. 현재 BitConverter 구현 유지.

### Revised Strategy

Phase 1.1 결과를 바탕으로 전략 수정:

1. **Phase 1.2 (버퍼 최적화)**: 더 높은 영향력 예상 - I/O 호출 감소
2. **Phase 1.3 (StringPool 통합)**: 메모리 최적화 - 문자열 중복 제거
3. **Phase 1.4 (MftCompactRecord)**: 메모리 레이아웃 최적화

MftParserV2.cs는 향후 동기식 대량 처리 시나리오를 위해 유지.

### Phase 1.2 Completed (2026-01-13)

**구현 완료:**
- MftReaderOptions 클래스 생성
- 기본 버퍼 크기: 64KB → 1MB (16배 증가)
- 버퍼 크기 범위: 64KB ~ 4MB
- 4KB 경계 자동 정렬
- 시스템 메모리 기반 CreateOptimal() 팩토리

**테스트:**
- MftReaderOptionsTests: 12개 단위 테스트
- MftBufferSizeTests: 13개 버퍼 테스트
- 전체 MFT 테스트: 35개 통과, 2개 스킵

### Phase 1.3 Completed (2026-01-13)

**구현 완료:**
- .NET 9+ `GetAlternateLookup<ReadOnlySpan<char>>()` 활용
- `InternFromSpan()`: 캐시 히트 시 zero-allocation
- `TryGetFromSpan()`: 존재 여부 확인 (할당 없음)
- `MftParserV2.TryParseUsnRecordPooled` 업데이트

**테스트:**
- StringPoolSpanTests: 13개 테스트 통과
- MFT 시뮬레이션: 500K 레코드 처리 검증
- 전체: 48개 통과, 2개 스킵

**성능 특성:**
- 캐시 히트: zero-allocation (Span lookup)
- 캐시 미스: string 생성 후 인터닝
- 중복 파일명에서 최대 효과 (예: index.html, README.md)

### Next Steps
1. **Phase 1.4**: MftCompactRecord 구조체 (40 bytes)
2. **실제 성능 측정**: 관리자 권한으로 실제 MFT 열거 테스트
3. **Phase 2.x**: I/O 및 동시성 최적화 (향후)

### Ready for Implementation
```bash
# Verify Phase 1.3
dotnet test --filter "Suite=MFT|Suite=StringPool" -c Release

# Next: Phase 1.4 MftCompactRecord
# - 40 bytes 구조체 설계
# - StringPool ID 기반 파일명 저장
# - 메모리 사용량 30% 절감
```
