using FastFind.Indexing;
using AwesomeAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// The measurements an engine reports about its own searches.
/// </summary>
[Trait("Category", "Functional")]
public class SearchTimingTallyTests
{
    [Fact]
    public void With_No_Searches_Every_Figure_Is_Zero()
    {
        var snapshot = new SearchTimingTally().Read();

        snapshot.Searches.Should().Be(0);
        snapshot.Matches.Should().Be(0);
        snapshot.Total.Should().Be(TimeSpan.Zero);
        snapshot.Average.Should().Be(TimeSpan.Zero, "not a made-up figure");
        snapshot.Fastest.Should().Be(TimeSpan.Zero);
        snapshot.Slowest.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void It_Reports_The_Total_Mean_Fastest_And_Slowest_Of_What_It_Was_Given()
    {
        var tally = new SearchTimingTally();
        tally.Record(TimeSpan.FromMilliseconds(2), matches: 5);
        tally.Record(TimeSpan.FromMilliseconds(8), matches: 1);
        tally.Record(TimeSpan.FromMilliseconds(5), matches: 0);

        var snapshot = tally.Read();

        snapshot.Searches.Should().Be(3);
        snapshot.Matches.Should().Be(6);
        snapshot.Total.Should().Be(TimeSpan.FromMilliseconds(15));
        snapshot.Average.Should().Be(TimeSpan.FromMilliseconds(5));
        snapshot.Fastest.Should().Be(TimeSpan.FromMilliseconds(2));
        snapshot.Slowest.Should().Be(TimeSpan.FromMilliseconds(8));
        snapshot.LastSearchUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        snapshot.LastSearchUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void A_Search_Faster_Than_A_Millisecond_Is_Not_Rounded_Away()
    {
        var tally = new SearchTimingTally();
        tally.Record(TimeSpan.FromMicroseconds(400), matches: 1);

        tally.Read().Total.Should().Be(TimeSpan.FromMicroseconds(400));
        tally.Read().Fastest.Should().Be(TimeSpan.FromMicroseconds(400));
    }

    [Fact]
    public void Reset_Forgets_Everything()
    {
        var tally = new SearchTimingTally();
        tally.Record(TimeSpan.FromMilliseconds(3), matches: 2);

        tally.Reset();

        var snapshot = tally.Read();
        snapshot.Searches.Should().Be(0);
        snapshot.Total.Should().Be(TimeSpan.Zero);
        snapshot.Fastest.Should().Be(TimeSpan.Zero);
        snapshot.LastSearchUtc.Should().Be(default);
    }

    [Fact]
    public async Task Concurrent_Recording_Loses_Nothing()
    {
        var tally = new SearchTimingTally();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) tally.Record(TimeSpan.FromTicks(10), matches: 1);
        })));

        var snapshot = tally.Read();
        snapshot.Searches.Should().Be(8000);
        snapshot.Matches.Should().Be(8000);
        snapshot.Total.Should().Be(TimeSpan.FromTicks(80_000));
    }
}
