using System.Runtime.CompilerServices;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Mft;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// An indexing run is bounded only when asked to be, and a run cut short says so.
/// </summary>
[Trait("Category", "Functional")]
public class IndexingTimeoutTests
{
    [Fact]
    public void An_Engine_Should_Have_No_Indexing_Time_Limit_By_Default()
    {
        WindowsSearchEngineOptions? used = null;
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngine(o => used = o, null, ProviderMode.Standard);

        // It was 30 seconds, gated on managed heap in use — so 30 on every machine, well short of
        // a whole-drive run.
        used!.IndexingTimeout.Should().BeNull();
        new WindowsSearchEngineOptions().IndexingTimeout.Should().BeNull();
    }

    [Fact]
    public void The_Obsolete_FileOperationTimeout_Should_Forward_To_IndexingTimeout()
    {
#pragma warning disable CS0618 // exercising the obsolete alias on purpose
        var bounded = new WindowsSearchEngineOptions { FileOperationTimeout = TimeSpan.FromMinutes(5) };
        var unbounded = new WindowsSearchEngineOptions { FileOperationTimeout = Timeout.InfiniteTimeSpan };

        bounded.IndexingTimeout.Should().Be(TimeSpan.FromMinutes(5));
        unbounded.IndexingTimeout.Should().BeNull();
        new WindowsSearchEngineOptions().FileOperationTimeout.Should().Be(Timeout.InfiniteTimeSpan);
#pragma warning restore CS0618
    }

    [Fact]
    public void A_Non_Positive_Timeout_Should_Fail_Validation()
    {
        new WindowsSearchEngineOptions { IndexingTimeout = TimeSpan.Zero }.Validate().IsValid.Should().BeFalse();
        new WindowsSearchEngineOptions { IndexingTimeout = null }.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_Run_Cut_Short_By_The_Timeout_Should_Report_Failure_And_Keep_What_It_Indexed()
    {
        using var engine = new WindowsSearchEngineImpl(
            new EndlessProvider(),
            new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance),
            new WindowsSearchEngineOptions { IndexingTimeout = TimeSpan.FromMilliseconds(400), EnableRealtimeMonitoring = false },
            NullLogger<WindowsSearchEngineImpl>.Instance);

        var phases = new List<IndexingProgressEventArgs>();
        engine.IndexingProgressChanged += (_, e) => { lock (phases) phases.Add(e); };

        await engine.StartIndexingAsync(new IndexingOptions { SpecificDirectories = { @"C:\endless" }, EnableMonitoring = false });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (engine.IsIndexing && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        engine.IsIndexing.Should().BeFalse("the timeout must end the run");
        lock (phases)
        {
            phases.Should().ContainSingle(p => p.Phase == IndexingPhase.Failed)
                .Which.CurrentPath.Should().Contain("timed out").And.Contain(nameof(WindowsSearchEngineOptions.IndexingTimeout));
            phases.Should().NotContain(p => p.Phase == IndexingPhase.Completed);
        }

        engine.TotalIndexedFiles.Should().BeGreaterThan(0);
        var found = await engine.SearchAsync(new SearchQuery { SearchText = "item-0.txt", SearchFileNameOnly = true });
        found.TotalMatches.Should().Be(1, "what was indexed before the cut stays searchable");
    }

    /// <summary>Enumerates forever, slowly, honouring cancellation.</summary>
    private sealed class EndlessProvider : IFileSystemProvider
    {
        public PlatformType SupportedPlatform => PlatformType.Windows;
        public bool IsAvailable => true;

        public async IAsyncEnumerable<FileItem> EnumerateFilesAsync(
            IEnumerable<string> locations, IndexingOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; ; i++)
            {
                await Task.Delay(10, cancellationToken);
                yield return new FileItem
                {
                    FullPath = $@"C:\endless\item-{i}.txt",
                    Name = $"item-{i}.txt",
                    DirectoryPath = @"C:\endless",
                    Extension = ".txt",
                    Attributes = FileAttributes.Archive,
                    DriveLetter = 'C',
                };
            }
        }

        public Task<FileItem?> GetFileInfoAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<FileItem?>(null);

        public Task<IEnumerable<FastFind.Interfaces.DriveInfo>> GetAvailableLocationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Enumerable.Empty<FastFind.Interfaces.DriveInfo>());

        public async IAsyncEnumerable<FileChangeEventArgs> MonitorChangesAsync(
            IEnumerable<string> locations, MonitoringOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult("NTFS");
        public ProviderPerformance GetPerformanceInfo() => new();
        public void Dispose() { }
    }
}
