using FastFind.Models;
using FastFind.Windows;
using FluentAssertions;

namespace FastFind.Windows.Tests;

public class MaxFileCountIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public MaxFileCountIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FastFindCapTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, true);

    [Fact]
    public async Task StartIndexingAsync_WithMaxFileCount_StopsAtCap()
    {
        if (!OperatingSystem.IsWindows()) return;

        WindowsRegistration.EnsureRegistered();
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngineForTesting();

        for (int i = 0; i < 20; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"file_{i:D3}.txt"), "x");

        var phaseReceived = new List<IndexingPhase>();
        engine.IndexingProgressChanged += (_, e) => phaseReceived.Add(e.Phase);

        var options = new IndexingOptions
        {
            SpecificDirectories = { _tempDir },
            MaxFileCount = 10,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            EnableMonitoring = false
        };

        await engine.StartIndexingAsync(options);
        await WaitForNotIndexing(engine);

        engine.TotalIndexedFiles.Should().BeLessThanOrEqualTo(10);
        phaseReceived.Should().Contain(IndexingPhase.CapReached);
        phaseReceived.Should().NotContain(IndexingPhase.Completed);
    }

    [Fact]
    public async Task StartIndexingAsync_WithoutMaxFileCount_IndexesAllFiles()
    {
        if (!OperatingSystem.IsWindows()) return;

        WindowsRegistration.EnsureRegistered();
        using var engine = WindowsSearchEngine.CreateWindowsSearchEngineForTesting();

        for (int i = 0; i < 10; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"file_{i:D3}.txt"), "x");

        var phaseReceived = new List<IndexingPhase>();
        engine.IndexingProgressChanged += (_, e) => phaseReceived.Add(e.Phase);

        var options = new IndexingOptions
        {
            SpecificDirectories = { _tempDir },
            MaxFileCount = null,
            ExcludedPaths = new List<string>(),
            ExcludedExtensions = new List<string>(),
            EnableMonitoring = false
        };

        await engine.StartIndexingAsync(options);
        await WaitForNotIndexing(engine);

        engine.TotalIndexedFiles.Should().Be(10);
        phaseReceived.Should().Contain(IndexingPhase.Completed);
        phaseReceived.Should().NotContain(IndexingPhase.CapReached);
    }

    private static async Task WaitForNotIndexing(FastFind.Interfaces.ISearchEngine engine, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (engine.IsIndexing && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}
