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

## Query latency

How long a query takes against an index that is already built. Taken 2026-11-06, after 2.4.0.

```bash
dotnet run -c Release --project src/FastFind.Benchmarks -- --query-latency
dotnet run -c Release --project src/FastFind.Benchmarks -- --query-latency --tree C:\some\folder
```

Each query is timed over 20–500 calls (at least 5 for a real tree) after 5 warm-up calls, and asks for at most 100
results. The corpus is synthetic and deterministic — names from a small vocabulary, a skewed extension mix, a hundred
files to a directory — and a "rare" word is the same *fraction* of the corpus at every size. Each configuration runs in its
own process. The Windows and Unix indexes are both measured on this Windows host: the structures are the same on every
platform; a Linux disk is not part of this.

**These figures are ranges, and the range is the finding.** The machine was shared with a long-running application and
the antivirus while this ran, and the same code measured several times gave results that differ by a factor of two or more
for the queries that read the whole corpus. Every cell below is the lowest and highest *median* (milliseconds) over the
runs; a single run's median and 95th percentile can be read from the command above. The Windows rows are from four or five
runs, the Unix rows from three. No run was left out.

**Windows in-memory index** — median of a query, lowest to highest across runs

| query | 100,000 | 300,000 | 1,000,000 |
|---|---:|---:|---:|
| name contains a rare word (~0.005% of files) | 21 – 37 | 61 – 192 | 213 – 310 |
| name contains a word nothing has | 16 – 47 | 49 – 181 | 173 – 320 |
| name contains a common word (~3% of files) | 0.6 – 0.7 | 0.5 – 1.4 | 0.6 – 0.8 |
| extension only (.json, ~10% of files) | 0.1 – 0.2 | 0.1 – 0.2 | 0.1 – 0.1 |
| common word + extension (.pdf) | 6.9 – 7.5 | 6.2 – 14.6 | 7.0 – 7.8 |
| full path contains a rare directory name (~0.05% of directories) | 2.2 – 23.7 | 5.0 – 63.3 | 0.1 – 66.1 |
| common word, scoped to one directory | 0.2 – 0.6 | 0.6 – 2.5 | 1.5 – 6.0 |

Matches returned, in row order — 100,000: 5, 0, 100, 100, 100, 100, 4 · 300,000: 15, 0, 100, 100, 100, 100, 4 ·
1,000,000: 50, 0, 100, 100, 100, 100, 4 (the cap is 100).

**Unix engine's own index** (same corpus, same host, three runs)

| query | 100,000 | 300,000 | 1,000,000 |
|---|---:|---:|---:|
| name contains a rare word | 20 – 25 | 59 – 67 | 223 – 485 |
| name contains a word nothing has | 17 – 20 | 49 – 50 | 173 – 388 |
| name contains a common word | 19 – 27 | 53 – 58 | 201 – 372 |
| extension only (.json) | 9 – 9 | 26 – 28 | 90 – 272 |
| common word + extension (.pdf) | 10 – 10 | 31 – 32 | 104 – 148 |
| full path contains a rare directory name | 28 – 30 | 83 – 99 | 291 – 371 |
| common word, scoped to one directory | 0.1 – 0.1 | 0.4 – 0.4 | 1.4 – 1.7 |

The largest spread is at 1,000,000 entries, where these queries materialise tens of thousands of matches (below); the
cause was not isolated, and collecting those lists is the likeliest.

Matches the index produced, in row order — 100,000: 5, 0, 3,088, 9,984, 135, 100, 4 · 300,000: 15, 0, 9,265, 29,609, 469, 200, 4 ·
1,000,000: 50, 0, 30,807, 99,706, 1,541, 500, 4.

**What these say**

- A query that finds its first 100 matches early answers in **about a millisecond or less at every size** measured
  (a common word, an extension, a directory) on the Windows index.
- A query that has to read the whole corpus — a rare word, or one nothing has — costs time in proportion to the corpus:
  roughly **20–50 ms at 100,000 entries, 50–190 ms at 300,000, and 170–320 ms at 1,000,000** on the Windows index, about
  0.2–0.3 µs an entry. For a text query nothing narrows the candidates before the text is compared; an extension or a
  directory does, which is why those rows are fast.
- The two engines do not do the same work for the same query. The Windows index stops at the requested number of
  results. The Unix engine collects **every** match and truncates afterwards — 30,807 matches for a query that asked for
  100, at 1,000,000 entries — which is why its common-word query costs 200–370 ms where the Windows one costs under
  two milliseconds, and why `TotalMatches` means "all matches" on one engine and "at most the cap" on the other.
- **Queries that stop at their cap are unpredictable.** Which 100 matches come first depends on the order the index
  enumerates its entries, which changes with each process: the rare-directory row above spans 0.1 to 66 ms at one size.
  Do not read a single figure for those rows.

**A query with fewer than five matches used to pay about 130 ms for a disk scan with nothing to scan.** The index treats
"fewer than five matches" as a sign it may be incomplete and walks the disk to check. The workers of that walk decided it
was finished by polling — ten 10 ms sleeps on an empty queue — so the walk could not take less than about 100 ms *even
when it had no directory to look at*. Isolated by switching the fallback off in a throwaway experiment, the
absent-word query measured **18.5 ms at 100,000 entries without it against 153.6 ms with it, and 190.0 ms against 323.4 ms
at 1,000,000** — a difference of about 135 ms at both sizes. That cost is now gone; it shows most where the rest of the
query is cheap:

| query, over a real tree | before | after (lowest – highest, three runs) |
|---|---:|---:|
| `C:\Windows\System32` (27,149 entries), a word nothing has | 154.3 | 6.7 – 11.0 |
| `C:\Windows\System32`, a word with 2 matches | 168.4 | 6.7 – 12.3 |
| `C:\Windows` (306,838 entries), a word nothing has | 275.7 | 120 – 130 |

Over a million-entry index the same ~130 ms is inside the machine's own spread (173–320 ms), so it is not claimed there.

**Over a real tree** (the engine as this host configures it — the change-journal provider, elevated — then queried through
`SearchAsync`; median, lowest – highest over three runs):

| query | `C:\Windows\System32` (27,149 entries) | `C:\Windows` (306,838 entries) |
|---|---:|---:|
| name contains a very common letter | 0.12 – 0.14 | 0.13 – 0.15 |
| name contains an uncommon word (`kernel32`: 2 matches / 100) | 6.7 – 12.3 | 111 – 124 |
| name contains a word nothing has | 6.7 – 11.0 | 120 – 130 |
| extension only (.dll) | 0.16 – 0.24 | 0.19 – 0.20 |

Indexing took 13–77 s: a change-journal pass reads the whole volume whichever folder was asked for, so the time is the
volume's, not the folder's, and it varies with what else the volume is doing. These two folders exist on this machine
only; `--tree` takes any folder.

**Not measured:** a corpus of real file names (the vocabulary here is small and the names are short), concurrent
queries, queries while indexing, regular-expression queries, a quiet machine, and any machine other than this one.

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
