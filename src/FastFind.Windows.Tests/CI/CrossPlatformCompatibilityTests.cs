using FastFind;
using FastFind.Interfaces;
using FastFind.Models;
using FastFind.Windows;
using FluentAssertions;

namespace FastFind.Windows.Tests.CI;

/// <summary>
/// CI/CD 호환성을 위한 크로스 플랫폼 테스트
/// </summary>
[Collection("CI")]
public class CrossPlatformCompatibilityTests
{
    [Fact]
    [Trait("Category", "CI")]
    public void FastFinder_Should_Be_Available()
    {
        // Arrange & Act
        var availablePlatforms = FastFinder.GetAvailablePlatforms();

        // Assert
        availablePlatforms.Should().NotBeNull();
        availablePlatforms.Should().NotBeEmpty("At least one platform should be available");

        Console.WriteLine($"Available platforms: {string.Join(", ", availablePlatforms)}");
    }

    [Fact]
    [Trait("Category", "CI")]
    public void Windows_Registration_Should_Work_On_Windows_Only()
    {
        // Act & Assert
        if (OperatingSystem.IsWindows())
        {
            // Windows에서는 등록이 성공해야 함
            Action registration = () => WindowsRegistration.EnsureRegistered();
            registration.Should().NotThrow("Windows registration should succeed on Windows");

            // 다시 등록해도 안전해야 함
            registration.Should().NotThrow("Multiple registrations should be safe");

            Console.WriteLine("Windows registration successful");
        }
        else
        {
            Console.WriteLine("Skipping Windows registration on non-Windows platform");
        }
    }

    [Fact]
    [Trait("Category", "CI")]
    public void System_Validation_Should_Handle_All_Environments()
    {
        // Act
        var validation = FastFinder.ValidateSystem();

        // Assert
        validation.Should().NotBeNull("System validation should never be null");
        validation.RuntimeVersion.Should().NotBeNullOrEmpty("Runtime version should be detected");

        // Platform detection should work
        if (OperatingSystem.IsWindows())
        {
            validation.Platform.Should().Be(PlatformType.Windows);
        }
        else
        {
            // Unix 플랫폼에서는 Windows가 아니어야 함
            validation.Platform.Should().NotBe(PlatformType.Windows);
        }

        Console.WriteLine($"Platform: {validation.Platform}");
        Console.WriteLine($"Runtime: {validation.RuntimeVersion}");
        Console.WriteLine($"Memory: {validation.AvailableMemory:N0} bytes");
        Console.WriteLine($"Summary: {validation.GetSummary()}");
    }

    [Fact]
    [Trait("Category", "CI")]
    public void Factory_Should_Handle_Unsupported_Platforms_Gracefully()
    {
        // Act & Assert
        if (OperatingSystem.IsWindows())
        {
            // Windows에서는 생성이 가능해야 함
            WindowsRegistration.EnsureRegistered();

            using var engine = FastFinder.CreateSearchEngine(PlatformType.Windows);
            engine.Should().NotBeNull("Windows search engine should be created on Windows");

            Console.WriteLine("Windows search engine created successfully");
        }
        else
        {
            // 비Windows에서는 예외가 발생하거나 null이 반환될 수 있음
            Action createEngine = () => FastFinder.CreateSearchEngine(PlatformType.Windows);

            // 예외 발생은 정상적인 동작임
            Console.WriteLine("Attempting Windows search engine creation on non-Windows platform");

            try
            {
                var engine = FastFinder.CreateSearchEngine(PlatformType.Windows);
                if (engine == null)
                {
                    Console.WriteLine("Windows search engine creation returned null (expected on non-Windows)");
                }
                else
                {
                    Console.WriteLine("Windows search engine unexpectedly created on non-Windows platform");
                    engine.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Windows search engine creation threw exception (expected): {ex.GetType().Name}");
                // 예외는 정상적인 동작임
            }
        }
    }

    [Fact]
    [Trait("Category", "CI")]
    public void Framework_Constants_Should_Be_Consistent()
    {
        // Act & Assert - 기본적인 상수들이 올바른지 확인
        var availablePlatforms = FastFinder.GetAvailablePlatforms();

        availablePlatforms.Should().NotBeNull();
        availablePlatforms.Should().NotBeEmpty();

        // Windows가 사용 가능한 플랫폼인지 확인
        if (OperatingSystem.IsWindows())
        {
            availablePlatforms.Should().Contain(PlatformType.Windows,
                "Windows should be available when running on Windows");
        }

        Console.WriteLine("Framework constants validation completed");
    }

    [Fact]
    [Trait("Category", "CI")]
    public void Memory_And_Performance_Constants_Should_Be_Reasonable()
    {
        // Act
        var validation = FastFinder.ValidateSystem();

        // Assert - 메모리가 너무 적지 않은지 확인 (CI 환경 고려)
        validation.AvailableMemory.Should().BeGreaterThan(0, "Available memory should be detected");

        // CI 환경에서는 메모리가 제한될 수 있으므로 경고만 출력
        if (validation.AvailableMemory < 100_000_000) // 100MB
        {
            Console.WriteLine($"Warning: Low memory detected in CI environment: {validation.AvailableMemory:N0} bytes");
        }
        else
        {
            Console.WriteLine($"Sufficient memory available: {validation.AvailableMemory:N0} bytes");
        }

        // 런타임 호환성 확인
        validation.IsCompatibleRuntime.Should().BeTrue("Runtime should always be compatible in CI");

        Console.WriteLine("Memory and performance validation completed");
    }
}