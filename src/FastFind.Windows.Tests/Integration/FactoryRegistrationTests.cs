using FastFind;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Integration;

/// <summary>
/// Tests for factory registration mechanism (Issue #6 validation)
/// Verifies that platform-specific search engines are properly registered and accessible.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Issue", "6")]
public class FactoryRegistrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public FactoryRegistrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        // Cleanup if needed
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public void CreateSearchEngine_OnWindows_ShouldSucceedWithoutManualRegistration()
    {
        // Arrange
        // Note: In test project, FastFind.Windows is already referenced, so assembly is loaded.
        // This test validates the auto-loading mechanism works as expected.

        // Act
        var engine = FastFinder.CreateSearchEngine();

        // Assert
        engine.Should().NotBeNull("CreateSearchEngine should return a valid engine instance");
        engine.Should().BeAssignableTo<ISearchEngine>("Engine should implement ISearchEngine");

        _output.WriteLine($"Successfully created search engine: {engine.GetType().FullName}");
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public void CreateWindowsSearchEngine_ShouldSucceedWithoutManualRegistration()
    {
        // Arrange & Act
        var engine = FastFinder.CreateWindowsSearchEngine();

        // Assert
        engine.Should().NotBeNull("CreateWindowsSearchEngine should return a valid engine instance");

        _output.WriteLine($"Successfully created Windows search engine: {engine.GetType().FullName}");
    }

    [Fact]
    public void ValidateSystem_OnWindows_ShouldShowPlatformSupported()
    {
        // Arrange & Act
        var validation = FastFinder.ValidateSystem();

        // Assert
        validation.Should().NotBeNull();
        validation.IsSupported.Should().BeTrue("Windows platform should be supported");
        validation.Platform.Should().Be(PlatformType.Windows);
        validation.IsReady.Should().BeTrue("System should be ready for FastFind");

        _output.WriteLine($"Validation: {validation.GetSummary()}");
        _output.WriteLine($"Runtime: {validation.RuntimeVersion}");
        _output.WriteLine($"Available Memory: {validation.AvailableMemory / (1024 * 1024)} MB");
    }

    [Fact]
    public void GetAvailablePlatforms_ShouldIncludeWindows()
    {
        // Arrange & Act
        var platforms = FastFinder.GetAvailablePlatforms().ToList();

        // Assert
        platforms.Should().Contain(PlatformType.Windows,
            "Windows factory should be registered via ModuleInitializer or auto-loading");

        _output.WriteLine($"Available platforms: {string.Join(", ", platforms)}");
    }

    [Fact]
    public void GetCurrentPlatform_OnWindows_ShouldReturnWindows()
    {
        // Arrange & Act
        var platform = FastFinder.GetCurrentPlatform();

        // Assert
        platform.Should().Be(PlatformType.Windows, "Running on Windows should return Windows platform");
    }

    [Fact]
    public void WindowsRegistration_EnsureRegistered_ShouldBeIdempotent()
    {
        // Arrange
        var initialPlatforms = FastFinder.GetAvailablePlatforms().ToList();

        // Act - Call multiple times
        WindowsRegistration.EnsureRegistered();
        WindowsRegistration.EnsureRegistered();
        WindowsRegistration.EnsureRegistered();

        var afterPlatforms = FastFinder.GetAvailablePlatforms().ToList();

        // Assert
        afterPlatforms.Should().BeEquivalentTo(initialPlatforms,
            "Multiple calls to EnsureRegistered should not change the factory list");
        afterPlatforms.Should().Contain(PlatformType.Windows);
    }

    [Fact]
    public void ModuleInitializer_ShouldRegisterFactoryOnAssemblyLoad()
    {
        // Arrange
        // This test verifies that the ModuleInitializer in FastFind.Windows
        // has properly registered the Windows factory.

        // Act
        // Just accessing the type forces the assembly to be loaded
        var type = typeof(WindowsSearchEngine);
        var platforms = FastFinder.GetAvailablePlatforms().ToList();

        // Assert
        type.Should().NotBeNull();
        platforms.Should().Contain(PlatformType.Windows,
            "ModuleInitializer should have registered Windows factory when assembly was loaded");

        _output.WriteLine($"WindowsSearchEngine type: {type.FullName}");
        _output.WriteLine($"Assembly: {type.Assembly.FullName}");
    }

    [Fact]
    public void CreateSearchEngine_WithLoggerFactory_ShouldSucceed()
    {
        // Arrange
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));

        // Act
        var engine = FastFinder.CreateSearchEngine(loggerFactory);

        // Assert
        engine.Should().NotBeNull("Engine should be created with logger factory");

        _output.WriteLine("Successfully created engine with custom logger factory");
    }

    [Fact]
    public void CreateSearchEngine_MultipleCalls_ShouldCreateNewInstances()
    {
        // Arrange & Act
        var engine1 = FastFinder.CreateSearchEngine();
        var engine2 = FastFinder.CreateSearchEngine();

        // Assert
        engine1.Should().NotBeNull();
        engine2.Should().NotBeNull();
        engine1.Should().NotBeSameAs(engine2, "Each call should create a new instance");

        // Cleanup
        engine1.Dispose();
        engine2.Dispose();
    }

    [Fact]
    [Trait("Category", "ThreadSafety")]
    public void CreateSearchEngine_ConcurrentCalls_ShouldAllSucceed()
    {
        // Arrange
        const int concurrentCalls = 10;
        var engines = new ISearchEngine[concurrentCalls];
        var exceptions = new Exception?[concurrentCalls];

        // Act
        Parallel.For(0, concurrentCalls, i =>
        {
            try
            {
                engines[i] = FastFinder.CreateSearchEngine();
            }
            catch (Exception ex)
            {
                exceptions[i] = ex;
            }
        });

        // Assert
        for (int i = 0; i < concurrentCalls; i++)
        {
            exceptions[i].Should().BeNull($"Call {i} should not throw exception");
            engines[i].Should().NotBeNull($"Call {i} should return valid engine");
        }

        _output.WriteLine($"Successfully created {concurrentCalls} engines concurrently");

        // Cleanup
        foreach (var engine in engines)
        {
            engine?.Dispose();
        }
    }
}
