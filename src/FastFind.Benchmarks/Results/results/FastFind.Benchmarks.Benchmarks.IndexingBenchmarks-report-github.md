```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7462/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-1360P 2.20GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.101
  [Host]   : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                  | Mean         | Error     | StdDev    | Median       | Ratio  | RatioSD | Rank | Gen0    | Allocated  | Alloc Ratio |
|---------------------------------------- |-------------:|----------:|----------:|-------------:|-------:|--------:|-----:|--------:|-----------:|------------:|
| DirectoryEnumerate_GetFiles             |     9.561 ms |  3.475 ms | 0.1905 ms |     9.551 ms |   1.00 |    0.02 |    1 | 46.8750 |  503.03 KB |        1.00 |
| DirectoryEnumerate_GetFileSystemEntries |     9.619 ms |  4.726 ms | 0.2590 ms |     9.531 ms |   1.01 |    0.03 |    1 | 46.8750 |  545.24 KB |        1.08 |
| SearchEngine_StartIndexing              | 2,119.807 ms | 95.924 ms | 5.2579 ms | 2,122.364 ms | 221.76 |    3.85 |    3 |       - | 5031.07 KB |       10.00 |
| FileInfo_Creation                       |    22.636 ms |  3.611 ms | 0.1979 ms |    22.700 ms |   2.37 |    0.04 |    2 | 31.2500 |  323.34 KB |        0.64 |
