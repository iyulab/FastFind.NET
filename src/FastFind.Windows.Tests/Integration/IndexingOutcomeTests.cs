using FastFind.Indexing;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// How an indexing pass ended, as the engine reports it in its statistics.
/// </summary>
/// <remarks>
/// A pass that lost a drive, or a location that was not there, used to end exactly like one that read
/// everything: the progress event said "completed" and the statistics said nothing, so a caller could not tell
/// an index that lacked part of the volume from a whole one.
/// </remarks>
[Trait("Category", "Functional")]
public class IndexingOutcomeTests
{
    private static WindowsSearchEngineImpl EngineOver(ScriptedFileSystemProvider provider) => new(
        provider,
        new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance),
        new WindowsSearchEngineOptions(),
        NullLogger<WindowsSearchEngineImpl>.Instance);

    private static IndexingOptions Options(Action<IndexingOptions>? configure = null)
    {
        var options = new IndexingOptions
        {
            SpecificDirectories = { @"C:\scripted" },
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
        };
        configure?.Invoke(options);
        return options;
    }

    private static async Task<IndexingStatistics> RunAsync(WindowsSearchEngineImpl engine, IndexingOptions options)
    {
        await engine.StartIndexingAsync(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing)
            await Task.Delay(10, timeout.Token);
        return await engine.GetIndexingStatisticsAsync();
    }

    private static ScriptedFileSystemProvider Provider(int items = 3)
    {
        var provider = new ScriptedFileSystemProvider();
        for (var i = 0; i < items; i++)
            provider.Items.Add(ScriptedFileSystemProvider.File($@"C:\scripted\f{i}.txt"));
        return provider;
    }

    [Fact]
    public async Task Before_Any_Pass_There_Is_No_Outcome()
    {
        using var engine = EngineOver(Provider());

        var stats = await engine.GetIndexingStatisticsAsync();

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.None);
        stats.FailedLocations.Should().BeEmpty();
        stats.IndexingOperations.Should().Be(0);
    }

    [Fact]
    public async Task A_Pass_That_Read_Everything_Is_Completed()
    {
        using var engine = EngineOver(Provider());

        var stats = await RunAsync(engine, Options());

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.Completed);
        stats.FailedLocations.Should().BeEmpty();
        stats.IndexingOperations.Should().Be(1, "one pass has run");
        stats.LastIndexingTime.Should().BeGreaterThan(TimeSpan.Zero, "the duration of the pass is reported, not left at zero");
    }

    [Fact]
    public async Task A_Pass_That_Could_Not_Read_A_Location_Is_Completed_With_Gaps_And_Names_It()
    {
        var provider = Provider();
        provider.Failures.Add(@"D:\gone");
        using var engine = EngineOver(provider);

        var stats = await RunAsync(engine, Options());

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.CompletedWithGaps);
        stats.FailedLocations.Should().Equal(@"D:\gone");
    }

    [Fact]
    public async Task A_Pass_Stopped_At_The_File_Cap_Says_So()
    {
        using var engine = EngineOver(Provider(items: 5));

        var stats = await RunAsync(engine, Options(o => o.MaxFileCount = 2));

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.CapReached);
    }

    [Fact]
    public async Task A_Pass_The_Caller_Stopped_Is_Cancelled()
    {
        var provider = new ScriptedFileSystemProvider(startOpen: false); // held: the pass cannot finish by itself
        using var engine = EngineOver(provider);

        await engine.StartIndexingAsync(Options());
        engine.IsIndexing.Should().BeTrue();
        await engine.StopIndexingAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.IsIndexing)
            await Task.Delay(10, timeout.Token);

        (await engine.GetIndexingStatisticsAsync()).LastRunOutcome.Should().Be(IndexingRunOutcome.Cancelled);
    }

    [Fact]
    public async Task A_Pass_That_Ended_On_An_Error_Is_Failed()
    {
        var provider = Provider();
        provider.ThrowAfterItems = new InvalidOperationException("the volume went away");
        using var engine = EngineOver(provider);

        var stats = await RunAsync(engine, Options());

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.Failed);
    }

    [Fact]
    public async Task The_Next_Pass_Reports_Itself_And_Not_The_Last()
    {
        var provider = Provider();
        provider.Failures.Add(@"D:\gone");
        using var engine = EngineOver(provider);
        (await RunAsync(engine, Options())).LastRunOutcome.Should().Be(IndexingRunOutcome.CompletedWithGaps);

        provider.Failures.Clear();
        var second = await RunAsync(engine, Options());

        second.LastRunOutcome.Should().Be(IndexingRunOutcome.Completed);
        second.FailedLocations.Should().BeEmpty();
        second.IndexingOperations.Should().Be(2);
    }
}
