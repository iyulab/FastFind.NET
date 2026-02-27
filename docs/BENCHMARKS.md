# FastFind.NET Performance Benchmarks

## Test Environment

| Component | Specification |
|-----------|---------------|
| **OS** | Windows 11 (Administrator) |
| **Runtime** | .NET 10.0 |
| **CPU** | Multi-core x64 |
| **Storage** | NTFS (C:, D:) |

---

## MFT (Master File Table) Performance

| Metric | Result |
|--------|--------|
| **Throughput** | 31,073 files/sec |
| **10K files** | 321 ms (cold start) |
| **vs Standard API** | 30-60x faster |

> MFT mode auto-activates with admin privileges on NTFS drives.

---

## SIMD String Matching

### Auto-Dispatch Architecture

| Tier | Instruction Set | Platforms | Chars/iteration |
|------|----------------|-----------|-----------------|
| Vector256 | AVX2 | x86-64 | 16 |
| Vector128 | SSE2 / NEON | x86-64, ARM64 | 8 |
| Scalar | — | All | 1 |

### Performance

| Metric | Result |
|--------|--------|
| **Operations/sec** | 1,877,459 (87% above 1M target) |
| **Memory per Op** | 439 bytes |
| **StringPool Interning** | 6,437 paths/sec, 60-80% memory reduction |

---

## Search Index

| Operation | Performance |
|-----------|-------------|
| **Indexing** | 243,856 files/sec |
| **Search** | 1,680,631 ops/sec |
| **FastFileItem Creation** | 202,347 items/sec |

---

## Linux (WSL)

| Component | Specification |
|-----------|---------------|
| **OS** | Ubuntu 24.04 (WSL2) |
| **Runtime** | .NET 10.0.103 |
| **File System** | ext4 |

| Test Category | Tests | Status |
|---------------|-------|--------|
| Factory Registration | 3 | Pass |
| LinuxFileSystemProvider | 12 | Pass |
| LinuxFileMonitor | 1 | Pass |
| E2E Integration | 3 | Pass |
| UnixSearchEngine | 10 | Pass |
| **Total** | **29** | **All Passed** |

Architecture: BoundedChannel(1000) BFS, depth ≤ 2 → queue dispatch, deeper → inline.

---

## Industry Comparison

| Tool | Enumeration Speed |
|------|-------------------|
| Everything (voidtools) | ~500K files/sec |
| **FastFind.NET MFT** | ~31K files/sec |
| Windows Search API | ~1K files/sec |

---

## Test Suite Summary

| Platform | Tests | Status |
|----------|-------|--------|
| Windows | 231 (13 skipped) | All Passed |
| Linux | 29 | All Passed |
| **Total** | **260** | **0 failures** |

---

## Running Benchmarks

```bash
# Functional tests
dotnet test src/FastFind.Windows.Tests --filter "Category!=Performance"
dotnet test src/FastFind.Unix.Tests

# BenchmarkDotNet
dotnet run --project src/FastFind.Benchmarks -c Release
dotnet run --project src/FastFind.Benchmarks -c Release -- --filter "*StringMatcher*"
```

---

**Last Updated**: February 2026
