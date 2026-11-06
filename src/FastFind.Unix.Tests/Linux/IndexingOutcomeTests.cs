using FastFind.Models;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

/// <summary>
/// How an indexing pass ended, as the Unix engine reports it in its statistics.
/// </summary>
/// <remarks>
/// A location that was not there used to leave the index short with nothing to say so, and the engine claimed one
/// indexing operation before any had run.
/// </remarks>
[Trait("Category", "Functional")]
public class IndexingOutcomeTests : IDisposable
{
    private readonly string _root;

    public IndexingOutcomeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastfind-outcome-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // The implementation is built directly: the Linux factory refuses to run anywhere but Linux, and these
    // tests are about the engine's bookkeeping, which is the same on every host.
    private static UnixSearchEngineImpl NewEngine() => new(new FastFind.Unix.Linux.LinuxFileSystemProvider());

    private IndexingOptions Options(params string[] extraLocations)
    {
        var options = new IndexingOptions
        {
            SpecificDirectories = { _root },
            EnableMonitoring = false,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            IncludeHidden = true,
            MaxFileSize = null,
        };
        foreach (var location in extraLocations) options.SpecificDirectories.Add(location);
        return options;
    }

    [Fact]
    public async Task Before_Any_Pass_There_Is_No_Outcome_And_No_Operation_Counted()
    {
        using var engine = NewEngine();

        var stats = await engine.GetIndexingStatisticsAsync();

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.None);
        stats.FailedLocations.Should().BeEmpty();
        stats.IndexingOperations.Should().Be(0, "it reported 1 before anything had been indexed");
    }

    [Fact]
    public async Task A_Pass_That_Read_Everything_Is_Completed()
    {
        using var engine = NewEngine();

        await engine.StartIndexingAsync(Options());
        var stats = await engine.GetIndexingStatisticsAsync();

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.Completed);
        stats.FailedLocations.Should().BeEmpty();
        stats.IndexingOperations.Should().Be(1);
    }

    [Fact]
    public async Task A_Location_That_Is_Not_There_Makes_The_Pass_Completed_With_Gaps_And_Is_Named()
    {
        using var engine = NewEngine();
        var missing = Path.Combine(_root, "no-such-directory");

        await engine.StartIndexingAsync(Options(missing));
        var stats = await engine.GetIndexingStatisticsAsync();

        stats.LastRunOutcome.Should().Be(IndexingRunOutcome.CompletedWithGaps);
        stats.FailedLocations.Should().Equal([missing]);
    }

    [Fact]
    public async Task A_Pass_Stopped_At_The_File_Cap_Says_So()
    {
        using var engine = NewEngine();
        var options = Options();
        options.MaxFileCount = 1;

        await engine.StartIndexingAsync(options);

        (await engine.GetIndexingStatisticsAsync()).LastRunOutcome.Should().Be(IndexingRunOutcome.CapReached);
    }

    [Fact]
    public async Task A_Pass_The_Caller_Stopped_Is_Cancelled()
    {
        using var engine = NewEngine();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await engine.StartIndexingAsync(Options(), cancelled.Token);

        (await engine.GetIndexingStatisticsAsync()).LastRunOutcome.Should().Be(IndexingRunOutcome.Cancelled);
    }

    [Fact]
    public async Task The_Next_Pass_Reports_Itself_And_Not_The_Last()
    {
        using var engine = NewEngine();
        await engine.StartIndexingAsync(Options(Path.Combine(_root, "no-such-directory")));
        (await engine.GetIndexingStatisticsAsync()).LastRunOutcome.Should().Be(IndexingRunOutcome.CompletedWithGaps);

        await engine.StartIndexingAsync(Options());
        var second = await engine.GetIndexingStatisticsAsync();

        second.LastRunOutcome.Should().Be(IndexingRunOutcome.Completed);
        second.FailedLocations.Should().BeEmpty();
        second.IndexingOperations.Should().Be(2);
    }
}
