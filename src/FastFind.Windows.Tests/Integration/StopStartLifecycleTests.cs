using FastFind.Models;
using FastFind.Windows;
using FastFind.Windows.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// Regression tests for Stop→Start lifecycle correctness in WindowsSearchEngineImpl.
/// Guards against ChannelClosedException recurrence after scheduled re-index cycles.
/// </summary>
[Collection("WindowsOnly")]
public class StopStartLifecycleTests : IDisposable
{
    private readonly string _testDir;

    public StopStartLifecycleTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"FastFind_LifecycleTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        // Seed a few files so indexing has something to find
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(_testDir, $"file{i}.txt"), $"content {i}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [WindowsOnlyFact]
    [Trait("Category", "Lifecycle")]
    public async Task StopThenStart_DoesNotThrow_OnSecondCycle()
    {
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngineForTesting(NullLoggerFactory.Instance);

        var options = new IndexingOptions { SpecificDirectories = [_testDir] };

        // First cycle
        await engine.StartIndexingAsync(options);
        await Task.Delay(200);
        await engine.StopIndexingAsync();

        // Second cycle — this is where ChannelClosedException was thrown before the fix
        var ex = await Record.ExceptionAsync(async () =>
        {
            await engine.StartIndexingAsync(options);
            await Task.Delay(200);
            await engine.StopIndexingAsync();
        });

        ex.Should().BeNull("Stop→Start should not throw after the first cycle");
    }

    [WindowsOnlyFact]
    [Trait("Category", "Lifecycle")]
    public async Task StopThenStart_RepeatedCycles_DoNotAccumulateErrors()
    {
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngineForTesting(NullLoggerFactory.Instance);

        var options = new IndexingOptions { SpecificDirectories = [_testDir] };

        // Three complete stop→start cycles (simulating midnight re-index over multiple days)
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var ex = await Record.ExceptionAsync(async () =>
            {
                await engine.StartIndexingAsync(options);
                await Task.Delay(200);
                await engine.StopIndexingAsync();
            });

            ex.Should().BeNull($"Cycle {cycle} should not throw");
        }
    }

    [WindowsOnlyFact]
    [Trait("Category", "Lifecycle")]
    public async Task StopThenStart_IndexedFilesAvailableAfterRestart()
    {
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngineForTesting(NullLoggerFactory.Instance);

        var options = new IndexingOptions { SpecificDirectories = [_testDir] };

        // First cycle
        await engine.StartIndexingAsync(options);
        await WaitForIndexingComplete(engine);
        await engine.StopIndexingAsync();

        // Second cycle
        await engine.StartIndexingAsync(options);
        await WaitForIndexingComplete(engine);

        // Search should still work after restart
        var result = await engine.SearchAsync(new SearchQuery { SearchText = "file" });
        result.HasError.Should().BeFalse("search should succeed after Stop→Start");
        result.TotalMatches.Should().BeGreaterThan(0, "indexed files should be searchable after restart");

        await engine.StopIndexingAsync();
    }

    [WindowsOnlyFact]
    [Trait("Category", "Lifecycle")]
    public async Task StopThenStart_MonitoringResumedAfterRestart()
    {
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngineForTesting(NullLoggerFactory.Instance);

        var options = new IndexingOptions
        {
            SpecificDirectories = [_testDir],
            // Note: Windows engine uses WindowsSearchEngineOptions.EnableRealtimeMonitoring (default true)
            // EnableMonitoring here is for Unix engine compatibility; Windows ignores it.
        };

        // First cycle
        await engine.StartIndexingAsync(options);
        await WaitForIndexingComplete(engine);
        await engine.StopIndexingAsync();

        // Second cycle
        await engine.StartIndexingAsync(options);
        await WaitForIndexingComplete(engine);

        // Monitoring should be active after restart (driven by WindowsSearchEngineOptions.EnableRealtimeMonitoring)
        engine.IsMonitoring.Should().BeTrue("monitoring should resume after Stop→Start");

        await engine.StopIndexingAsync();
    }

    [WindowsOnlyFact]
    [Trait("Category", "Lifecycle")]
    public async Task StopThenStart_FileChangeEvents_FiredAfterRestart()
    {
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngineForTesting(NullLoggerFactory.Instance);

        var options = new IndexingOptions
        {
            SpecificDirectories = [_testDir],
            // Note: Windows engine enables monitoring via WindowsSearchEngineOptions.EnableRealtimeMonitoring (default true)
        };

        // First cycle — stop cleanly
        await engine.StartIndexingAsync(options);
        await WaitForIndexingComplete(engine);
        await engine.StopIndexingAsync();

        // Second cycle — monitoring must be active
        var receivedEvents = new List<string>();
        engine.FileChanged += (_, args) => receivedEvents.Add(args.NewPath);

        await engine.StartIndexingAsync(options);
        await WaitForIndexingComplete(engine);

        // Give the monitoring watcher time to attach
        await Task.Delay(500);

        // Create a new file to trigger a change event
        var newFile = Path.Combine(_testDir, $"new_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(newFile, "trigger");

        // Wait for the event to be delivered
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (receivedEvents.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        await engine.StopIndexingAsync();

        receivedEvents.Should().NotBeEmpty(
            "FileChanged events should be fired after Stop→Start — " +
            "absence indicates the monitoring channel is permanently closed");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task WaitForIndexingComplete(FastFind.Interfaces.ISearchEngine engine, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (engine.IsIndexing && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}
