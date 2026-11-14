using FastFind.Windows.Mft;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FastFind.Windows.Tests.Mft;

/// <summary>
/// A reader of the change journal that falls behind must slow the monitor down, not grow a queue.
/// The channel the monitor hands records to was unbounded: it read the whole journal ahead of a reader
/// that does a file-system lookup per record. These tests drive the hand-off itself, so they need no
/// volume handle and no elevation.
/// </summary>
[Trait("Category", "Functional")]
public class UsnJournalMonitorBackpressureTests
{
    private static UsnChangeRecord Record(long usn) =>
        new(usn, 1, 5, UsnReason.FileCreate | UsnReason.Close, FileAttributes.Normal, $"f{usn}.txt", DateTime.UtcNow, 'C');

    [Fact]
    public async Task A_Full_Channel_Should_Make_The_Monitor_Wait_Instead_Of_Queueing_More()
    {
        await using var monitor = new UsnJournalMonitor(logger: null, pendingCapacity: 3);

        for (var i = 0; i < 3; i++)
            await monitor.PublishAsync(Record(i), 'C', CancellationToken.None);

        var fourth = monitor.PublishAsync(Record(3), 'C', CancellationToken.None).AsTask();
        await Task.Delay(150);
        fourth.IsCompleted.Should().BeFalse("the channel holds three records and nobody has read one");
        monitor.Changes.Count.Should().Be(3, "a waiting write must not be queued behind the bound");

        monitor.Changes.TryRead(out var first).Should().BeTrue();
        first.Usn.Should().Be(0);
        await fourth.WaitAsync(TimeSpan.FromSeconds(5));

        var rest = new List<long>();
        while (monitor.Changes.TryRead(out var r)) rest.Add(r.Usn);
        rest.Should().Equal([1L, 2L, 3L], "nothing is dropped and the order is kept");
    }

    [Fact]
    public async Task A_Waiting_Write_Should_End_With_The_Cancellation_That_Stops_The_Monitor()
    {
        await using var monitor = new UsnJournalMonitor(logger: null, pendingCapacity: 1);
        await monitor.PublishAsync(Record(0), 'C', CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var blocked = monitor.PublishAsync(Record(1), 'C', cts.Token).AsTask();
        await Task.Delay(100);
        cts.Cancel();

        await FluentActions.Awaiting(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_Event_Should_Fire_For_Every_Record_That_Is_Handed_Over()
    {
        await using var monitor = new UsnJournalMonitor(logger: null, pendingCapacity: 8);
        var seen = new List<long>();
        monitor.ChangeDetected += (_, e) => seen.Add(e.Record.Usn);

        for (var i = 0; i < 5; i++)
            await monitor.PublishAsync(Record(i), 'C', CancellationToken.None);

        seen.Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public async Task A_Stall_Should_Be_Reported_Once_And_Again_After_The_Reader_Catches_Up()
    {
        var log = new CountingLogger();
        await using var monitor = new UsnJournalMonitor(log, pendingCapacity: 1);

        await monitor.PublishAsync(Record(0), 'C', CancellationToken.None);
        var second = monitor.PublishAsync(Record(1), 'C', CancellationToken.None).AsTask();
        await Task.Delay(100);
        log.Warnings.Should().Be(1);

        monitor.Changes.TryRead(out _).Should().BeTrue();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Changes.TryRead(out _).Should().BeTrue();

        // an immediate write succeeds here, which ends the episode; the next stall is a new one
        await monitor.PublishAsync(Record(2), 'C', CancellationToken.None);
        var fourth = monitor.PublishAsync(Record(3), 'C', CancellationToken.None).AsTask();
        await Task.Delay(100);
        log.Warnings.Should().Be(2);

        monitor.Changes.TryRead(out _).Should().BeTrue();
        await fourth.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_Default_Bound_Should_Be_Finite()
    {
        UsnJournalMonitor.MaxPendingChanges.Should().BeInRange(1, 1_000_000);
    }

    private sealed class CountingLogger : ILogger<UsnJournalMonitor>
    {
        private int _warnings;
        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref _warnings);
        }
    }
}
