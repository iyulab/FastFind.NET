```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7462/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-1360P 2.20GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.101
  [Host]   : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | StringCount | Mean         | Error       | StdDev     | Median       | Ratio | RatioSD | Rank | Allocated | Alloc Ratio |
|------------------------------ |------------ |-------------:|------------:|-----------:|-------------:|------:|--------:|-----:|----------:|------------:|
| **Native_StringContains**         | **1000**        |     **8.096 μs** |   **5.3797 μs** |  **0.2949 μs** |     **8.199 μs** |  **1.00** |    **0.05** |    **2** |         **-** |          **NA** |
| Native_StringContains_Ordinal | 1000        |     5.659 μs |   0.5940 μs |  0.0326 μs |     5.650 μs |  0.70 |    0.02 |    1 |         - |          NA |
| Span_Contains                 | 1000        |     7.549 μs |   3.8003 μs |  0.2083 μs |     7.431 μs |  0.93 |    0.04 |    2 |         - |          NA |
| IndexOf_Ordinal               | 1000        |     6.033 μs |   1.8821 μs |  0.1032 μs |     5.976 μs |  0.75 |    0.03 |    1 |         - |          NA |
| IndexOf_OrdinalIgnoreCase     | 1000        |     7.740 μs |   2.7030 μs |  0.1482 μs |     7.795 μs |  0.96 |    0.03 |    2 |         - |          NA |
|                               |             |              |             |            |              |       |         |      |           |             |
| **Native_StringContains**         | **10000**       |   **161.174 μs** |  **21.6574 μs** |  **1.1871 μs** |   **160.747 μs** |  **1.00** |    **0.01** |    **2** |         **-** |          **NA** |
| Native_StringContains_Ordinal | 10000       |    82.316 μs |  15.7247 μs |  0.8619 μs |    82.042 μs |  0.51 |    0.01 |    1 |         - |          NA |
| Span_Contains                 | 10000       |   160.971 μs |  59.1732 μs |  3.2435 μs |   162.432 μs |  1.00 |    0.02 |    2 |         - |          NA |
| IndexOf_Ordinal               | 10000       |    82.221 μs |  14.5198 μs |  0.7959 μs |    82.620 μs |  0.51 |    0.01 |    1 |         - |          NA |
| IndexOf_OrdinalIgnoreCase     | 10000       |   179.742 μs |  29.0550 μs |  1.5926 μs |   180.516 μs |  1.12 |    0.01 |    2 |         - |          NA |
|                               |             |              |             |            |              |       |         |      |           |             |
| **Native_StringContains**         | **50000**       |   **958.197 μs** | **345.3760 μs** | **18.9312 μs** |   **962.353 μs** |  **1.00** |    **0.02** |    **3** |         **-** |          **NA** |
| Native_StringContains_Ordinal | 50000       |   623.595 μs | 253.0731 μs | 13.8718 μs |   617.087 μs |  0.65 |    0.02 |    1 |         - |          NA |
| Span_Contains                 | 50000       | 1,010.713 μs | 100.0415 μs |  5.4836 μs | 1,008.503 μs |  1.06 |    0.02 |    3 |         - |          NA |
| IndexOf_Ordinal               | 50000       |   771.828 μs | 254.0157 μs | 13.9235 μs |   777.495 μs |  0.81 |    0.02 |    2 |         - |          NA |
| IndexOf_OrdinalIgnoreCase     | 50000       | 1,198.086 μs | 756.8912 μs | 41.4878 μs | 1,199.909 μs |  1.25 |    0.04 |    3 |         - |          NA |
