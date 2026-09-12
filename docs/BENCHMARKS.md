# FastFind.NET Performance Benchmarks

**Every figure on this page names the command that produces it and the date it was taken.** A number
without both is not published here. Several that used to be were removed in 2.4.0 — see
[What was removed, and why](#what-was-removed-and-why) — because nothing in the repository
reproduced them, and where a measurement did exist it disagreed.

A performance or memory claim in this project needs **at least two corpus sizes** before it is
written down. One corpus size has reversed a result here before.

---

## Test environment

| Component | Specification |
|-----------|---------------|
| OS | Windows 11, elevated session |
| Runtime | .NET 10.0 |
| CPU | Multi-core x64 |
| Storage | NTFS |

Figures below are from one machine. They say what this library does here; they are not a claim about
your hardware.

---

## Memory retained per entry

The only memory figures this project stands behind. Taken 2026-09-12 on 2.4.0.

```bash
dotnet run -c Release --project src/FastFind.Benchmarks -- --memory-retention
```

Each configuration runs in its own process, because `StringPool` is static and a shared process
would let one measurement contaminate the next.

**What an in-memory index holds** — this is the table that describes an engine. An array of items
does not: the index adds its own keys and structure, and the gap between the two was once 4.6×.

| Index | Files per directory | Corpus | Per entry |
|---|---|---|---|
| Windows in-memory index | 100 | 100,000 | 387.7 B |
| Windows in-memory index | 100 | 300,000 | 385.0 B |
| Windows in-memory index | 1 | 100,000 | 756.8 B |
| Windows in-memory index | 1 | 300,000 | 768.9 B |
| Unix engine's index | 100 | 100,000 | 371.2 B |
| Unix engine's index | 100 | 300,000 | 379.7 B |
| Unix engine's index | 1 | 100,000 | 740.2 B |
| Unix engine's index | 1 | 300,000 | 763.4 B |

Files per directory is a **measured axis, not a constant**: a corpus with one file per directory
gives interning nothing to share, and roughly doubles what an entry costs. Real corpora sit nearer
the 100 row.

The Unix engine's index is measured on a Windows host here. Its structure is identical on every
platform, but the strings a Linux provider hands it are not these — for an end-to-end Linux figure,
index a real tree with the Unix engine.

**String interning** reduces the managed heap by a few per cent, not the 60–80% this page claimed
for years. The reason is structural: a file's full path is unique to it, so interning cannot
deduplicate the largest of the strings an item holds — which is why `FastFileItem` no longer retains
it and composes it on read instead.

---

## Enumeration on the Windows change journal

The Windows provider that needs elevation enumerates the NTFS **change journal**
(`FSCTL_ENUM_USN_DATA`). It does **not** parse the Master File Table. This matters to what it costs:
a journal record carries a name, a parent reference and attributes, and no size or timestamps — so
those are opt-in through `IndexingOptions.CollectFileMetadata` and cost one metadata query per
entry. Reading `$MFT` file records directly would give both at enumeration speed; that is future
work, not what ships.

Measured 2026-09-10 by the performance suite:

```bash
dotnet test --filter "Category=Performance&Suite=Integration"
```

| Measurement | Target asserted | Measured |
|---|---|---|
| Single-volume enumeration | ≥ 200,000 records/sec | 136,044 |
| Benchmark suite aggregate | ≥ 200,000 records/sec | 177,360 |
| Two volumes in parallel | ≥ 200,000 records/sec | 174,363 |
| Memory per record | ≤ 100 B | 213.3 B |

**These four tests fail, and the targets have not been moved to make them pass.** Whether the
targets are real or aspirational is an open question — one machine and one run is enough to say they
are not met here, not enough to say they are wrong. The rate target is documented in the test as
"40% of Everything (~500,000 rec/s)".

Also seen while measuring and still unexplained: two volumes enumerated **in parallel** give 174,363
records/sec, against **229,312** for the same two run separately and added. The parallel test
asserts a floor and never compares the two, so it says nothing about this.

---

## SIMD string matching

`SIMDStringMatcher` dispatches on what the hardware offers:

| Tier | Instruction set | Platforms | Chars per iteration |
|---|---|---|---|
| Vector256 | AVX2 | x86-64 | 16 |
| Vector128 | SSE2 / NEON | x86-64, ARM64 | 8 |
| Scalar | — | All | 1 |

Measured 2026-09-10, on a 10,000-character haystack with a 24-character pattern, **like for like** —
`OrdinalIgnoreCase` on both sides, because the vectorised path folds case itself:

| | `SIMDStringMatcher` | `string.Contains` |
|---|---|---|
| Time | 769 ms | **196 ms** |
| Allocated per operation | 8,224 B | — |

It is **3.9× slower** than the BCL call it exists to beat, which is itself vectorised, and it
allocates where a test expects under 1,024 B. The likely cause is that its verification loop folds
case one character at a time, outside the vector. Nothing has been changed on the strength of one
measurement; whether the type is worth keeping in this form is open.

Earlier editions of this page published 1,877,459 operations/sec here. See below.

---

## Test suites

Taken 2026-09-12 on 2.4.0. These are the functional runs — the gate CI enforces.

```bash
dotnet test src/FastFind.Windows.Tests --filter "Category!=Performance"
dotnet test src/FastFind.Unix.Tests    --filter "Category!=Performance"
```

| Platform | Passed | Failed | Skipped |
|---|---|---|---|
| Windows | 461 | 0 | 12 |
| Linux | 104 | 0 | 0 |

The performance category is excluded from CI and from these totals. Run it deliberately:

```bash
dotnet test --filter "Category=Performance"
```

As of 2026-09-10 it reports **5 failed, 47 passed, 12 skipped**; the failures are the assertions
tabulated above, against targets nobody has confirmed are real.

---

## What was removed, and why

These figures appeared on this page and are gone. Each was published without a command that
reproduces it, and in two cases a later measurement contradicted it. They are listed rather than
deleted quietly, because a reader who saw them deserves to know what happened to them.

| Removed | Why |
|---|---|
| SIMD "1,877,459 ops/sec (87% above 1M target)" | Measured like for like, the matcher is 3.9× **slower** than `string.Contains` |
| "StringPool interning: 60–80% memory reduction" | Measured at a few per cent across two corpus sizes |
| MFT "31,073 files/sec", "30–60× faster than the standard API" | No reproducible source; the suite measures the journal path in records/sec and does not support this |
| "Indexing 243,856 files/sec", "Search 1,680,631 ops/sec", "FastFileItem creation 202,347 items/sec" | No reproducible source |
| Industry comparison table (Everything, Windows Search) | Rested on the removed enumeration figure; no measurement of the other tools was ever taken here |
| "Windows 231 tests, Linux 29" | Stale by an order of magnitude |

---

## Running the benchmarks

```bash
# Functional suites (what CI gates on)
dotnet test src/FastFind.Windows.Tests --filter "Category!=Performance"
dotnet test src/FastFind.Unix.Tests    --filter "Category!=Performance"

# Performance category — excluded from CI, run deliberately
dotnet test --filter "Category=Performance"
dotnet test --filter "Category=Performance&Suite=SIMD"

# Memory retained per entry (not BenchmarkDotNet — each config needs its own process)
dotnet run -c Release --project src/FastFind.Benchmarks -- --memory-retention

# BenchmarkDotNet
dotnet run -c Release --project src/FastFind.Benchmarks
dotnet run -c Release --project src/FastFind.Benchmarks -- --filter "*StringMatcher*"
```

---

**Last updated**: 2026-09-12
