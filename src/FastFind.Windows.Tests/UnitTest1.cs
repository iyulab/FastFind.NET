using BenchmarkDotNet.Running;
using FastFind.Windows.Tests.Performance;

namespace FastFind.Windows.Tests;

/// <summary>
/// Main test runner and benchmark entry point
/// </summary>
public class TestRunner
{
    [Fact]
    public void FastFind_Should_Be_Available()
    {
        // Basic smoke test to verify FastFind is available
        var validation = FastFind.FastFinder.ValidateSystem();
        
        validation.Should().NotBeNull();
        Console.WriteLine($"Platform: {validation.Platform}");
        Console.WriteLine($"Ready: {validation.IsReady}");
        Console.WriteLine($"Summary: {validation.GetSummary()}");
    }
    
    /// <summary>
    /// Manual benchmark runner - call this to run performance benchmarks
    /// Usage: Create a separate console app or call from test explorer
    /// </summary>
    public static void RunBenchmarks()
    {
        Console.WriteLine("Starting FastFind.NET Performance Benchmarks...");
        
        try
        {
            BenchmarkRunner.Run<SearchPerformanceBenchmarks>();
            Console.WriteLine("Benchmarks completed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Benchmark error: {ex.Message}");
            throw;
        }
    }
}
