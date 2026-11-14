using FastFind.Models;
using AwesomeAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Functional")]
[Trait("OS", "Linux")]
public class MaxFileCountTests : IDisposable
{
    private readonly string _tempDir;

    public MaxFileCountTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FastFindCapTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, true);

    [LinuxFact]
    public async Task StartIndexingAsync_WithMaxFileCount_StopsAtCap()
    {
        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();

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

        engine.TotalIndexedFiles.Should().BeLessThanOrEqualTo(10);
        phaseReceived.Should().Contain(IndexingPhase.CapReached);
        phaseReceived.Should().NotContain(IndexingPhase.Completed);
    }

    [LinuxFact]
    public async Task StartIndexingAsync_WithoutMaxFileCount_IndexesAllFiles()
    {
        using var engine = UnixSearchEngine.CreateLinuxSearchEngine();

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

        engine.TotalIndexedFiles.Should().Be(10);
        phaseReceived.Should().Contain(IndexingPhase.Completed);
        phaseReceived.Should().NotContain(IndexingPhase.CapReached);
    }
}
