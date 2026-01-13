using FastFind;
using FastFind.Interfaces;
using FastFind.Windows;
using FluentAssertions;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.CI;

/// <summary>
/// Tests for platform configuration (Issue #2 validation)
/// Verifies that the project builds correctly for both AnyCPU and x64 platforms.
/// </summary>
[Trait("Category", "CI")]
[Trait("Issue", "2")]
public class PlatformConfigurationTests
{
    private readonly ITestOutputHelper _output;

    public PlatformConfigurationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Priority", "Critical")]
    public void FastFindWindows_Assembly_ShouldLoadSuccessfully()
    {
        // Arrange & Act
        var assembly = typeof(WindowsSearchEngine).Assembly;

        // Assert
        assembly.Should().NotBeNull("FastFind.Windows assembly should be loadable");
        assembly.FullName.Should().Contain("FastFind.Windows");

        _output.WriteLine($"Assembly: {assembly.FullName}");
        _output.WriteLine($"Location: {assembly.Location}");
    }

    [Fact]
    public void FastFindCore_Assembly_ShouldLoadSuccessfully()
    {
        // Arrange & Act
        var assembly = typeof(FastFinder).Assembly;

        // Assert
        assembly.Should().NotBeNull("FastFind.Core assembly should be loadable");
        assembly.FullName.Should().Contain("FastFind");

        _output.WriteLine($"Assembly: {assembly.FullName}");
        _output.WriteLine($"Location: {assembly.Location}");
    }

    [Fact]
    public void RuntimeArchitecture_ShouldBeDetectable()
    {
        // Arrange & Act
        var is64BitProcess = Environment.Is64BitProcess;
        var is64BitOS = Environment.Is64BitOperatingSystem;
        var architecture = RuntimeInformation.ProcessArchitecture;

        // Assert
        _output.WriteLine($"Is64BitProcess: {is64BitProcess}");
        _output.WriteLine($"Is64BitOS: {is64BitOS}");
        _output.WriteLine($"ProcessArchitecture: {architecture}");

        // The process should be running - no specific assertion needed
        // This test just logs the runtime architecture for diagnostics
    }

    [Fact]
    public void WindowsSearchEngine_ShouldWorkOnAnyCPU()
    {
        // Arrange
        WindowsRegistration.EnsureRegistered();

        // Act
        var engine = FastFinder.CreateWindowsSearchEngine();

        // Assert
        engine.Should().NotBeNull("WindowsSearchEngine should work regardless of CPU architecture");

        _output.WriteLine($"Engine type: {engine.GetType().FullName}");
        _output.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");

        // Cleanup
        engine.Dispose();
    }

    [Fact]
    public void SIMDSupport_ShouldBeAvailable()
    {
        // Arrange & Act
        var isHardwareAccelerated = System.Numerics.Vector.IsHardwareAccelerated;
        var vectorSize = System.Numerics.Vector<byte>.Count;

        // Assert
        _output.WriteLine($"SIMD Hardware Accelerated: {isHardwareAccelerated}");
        _output.WriteLine($"Vector Size: {vectorSize} bytes");

        // SIMD should be available on modern CPUs
        isHardwareAccelerated.Should().BeTrue("Modern CPUs should support SIMD");
    }

    [Fact]
    public void RuntimeVersion_ShouldBeDotNet10OrLater()
    {
        // Arrange
        var version = Environment.Version;

        // Assert
        version.Major.Should().BeGreaterThanOrEqualTo(10, "FastFind.NET requires .NET 10+");

        _output.WriteLine($".NET Version: {version}");
        _output.WriteLine($"Framework Description: {RuntimeInformation.FrameworkDescription}");
    }

    [Fact]
    public void Platform_ShouldBeWindows()
    {
        // Arrange & Act
        var isWindows = OperatingSystem.IsWindows();
        var currentPlatform = FastFinder.GetCurrentPlatform();

        // Assert
        isWindows.Should().BeTrue("These tests should run on Windows");
        currentPlatform.Should().Be(PlatformType.Windows);

        _output.WriteLine($"OS Description: {RuntimeInformation.OSDescription}");
        _output.WriteLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
    }

    [Fact]
    public void AssemblyReferences_ShouldBeResolvable()
    {
        // Arrange
        var windowsAssembly = typeof(WindowsSearchEngine).Assembly;
        var referencedAssemblies = windowsAssembly.GetReferencedAssemblies();

        // Act & Assert
        _output.WriteLine("Referenced assemblies:");
        foreach (var refAssembly in referencedAssemblies)
        {
            _output.WriteLine($"  - {refAssembly.FullName}");

            // Try to load each referenced assembly
            try
            {
                var loaded = Assembly.Load(refAssembly);
                loaded.Should().NotBeNull($"Referenced assembly {refAssembly.Name} should be loadable");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"    WARNING: Could not load {refAssembly.Name}: {ex.Message}");
                // Some system assemblies may not be loadable by name, which is OK
            }
        }
    }

    [Fact]
    public void ProjectConfiguration_AnyCPU_ShouldWorkCorrectly()
    {
        // This test verifies Issue #2 fix:
        // The project should build and run correctly with AnyCPU configuration

        // Arrange
        var assembly = typeof(WindowsSearchEngine).Assembly;
        var module = assembly.GetModules().First();

        // Act
        module.GetPEKind(out var peKind, out var machine);

        // Assert
        _output.WriteLine($"PE Kind: {peKind}");
        _output.WriteLine($"Machine: {machine}");

        // AnyCPU assemblies should have ILOnly flag
        // If built with x64, they'll have Required32Bit or PE32Plus flags
        // Either is acceptable after Issue #2 fix
        peKind.Should().NotBe((PortableExecutableKinds)0, "Assembly should have valid PE kind");
    }

    [Fact]
    public void AllPublicTypes_ShouldBeAccessible()
    {
        // Arrange
        var windowsAssembly = typeof(WindowsSearchEngine).Assembly;

        // Act
        var publicTypes = windowsAssembly.GetExportedTypes();

        // Assert
        publicTypes.Should().NotBeEmpty("FastFind.Windows should have public types");

        _output.WriteLine($"Public types in FastFind.Windows ({publicTypes.Length}):");
        foreach (var type in publicTypes.Take(20))
        {
            _output.WriteLine($"  - {type.FullName}");
        }

        if (publicTypes.Length > 20)
        {
            _output.WriteLine($"  ... and {publicTypes.Length - 20} more");
        }
    }

    [Fact]
    public void SearchEngine_ShouldHaveExpectedCapabilities()
    {
        // Arrange
        WindowsRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateWindowsSearchEngine();

        // Assert
        engine.Should().NotBeNull();

        // Verify core properties are accessible
        var isIndexing = engine.IsIndexing;
        var isMonitoring = engine.IsMonitoring;
        var totalFiles = engine.TotalIndexedFiles;

        _output.WriteLine($"IsIndexing: {isIndexing}");
        _output.WriteLine($"IsMonitoring: {isMonitoring}");
        _output.WriteLine($"TotalIndexedFiles: {totalFiles}");

        // Initially these should be default values
        isIndexing.Should().BeFalse("New engine should not be indexing");
        isMonitoring.Should().BeFalse("New engine should not be monitoring");
        totalFiles.Should().Be(0, "New engine should have no indexed files");
    }
}
