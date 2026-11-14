using FastFind.Models;
using FastFind.Windows.Implementation;
using AwesomeAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// The watcher's queue of waiting events has a bound: when the engine falls behind, events past it are
/// counted instead of piling up in memory.
/// </summary>
[Trait("Category", "Functional")]
public class PendingChangesTests
{
    private static FileChangeEventArgs Change(int i) => new(FileChangeType.Created, $@"C:\f{i}.txt");

    [Fact]
    public void Events_Past_The_Bound_Should_Be_Counted_And_Not_Kept()
    {
        var queue = new WindowsFileSystemProvider.PendingChanges(3);

        for (var i = 0; i < 5; i++) queue.Enqueue(Change(i));

        queue.TakeDropped().Should().Be(2);
        queue.TakeDropped().Should().Be(0, "the count is taken, not read");
        var kept = new List<string>();
        while (queue.TryDequeue(out var change)) kept.Add(change.NewPath);
        kept.Should().Equal(@"C:\f0.txt", @"C:\f1.txt", @"C:\f2.txt");
    }

    [Fact]
    public void Draining_The_Queue_Should_Make_Room_Again()
    {
        var queue = new WindowsFileSystemProvider.PendingChanges(2);
        queue.Enqueue(Change(0));
        queue.Enqueue(Change(1));
        queue.TryDequeue(out _).Should().BeTrue();

        queue.Enqueue(Change(2));

        queue.TakeDropped().Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_Writers_Should_Never_Hold_More_Than_The_Bound()
    {
        var queue = new WindowsFileSystemProvider.PendingChanges(100);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) queue.Enqueue(Change(t * 1000 + i));
        })));

        var kept = 0;
        while (queue.TryDequeue(out _)) kept++;
        kept.Should().Be(100);
        queue.TakeDropped().Should().Be(8 * 1000 - 100);
    }
}
