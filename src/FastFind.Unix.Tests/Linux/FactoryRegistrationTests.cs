using FastFind.Interfaces;
using FluentAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Integration")]
[Trait("OS", "Linux")]
public class FactoryRegistrationTests
{
    [Fact]
    public void EnsureRegistered_OnLinux_ShouldRegisterFactory()
    {
        if (!OperatingSystem.IsLinux()) return;

        UnixRegistration.EnsureRegistered();
        var platforms = FastFinder.GetAvailablePlatforms();
        platforms.Should().Contain(PlatformType.Linux);
    }

    [Fact]
    public void CreateSearchEngine_OnLinux_ShouldReturnEngine()
    {
        if (!OperatingSystem.IsLinux()) return;

        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();
        engine.Should().NotBeNull();
    }

    [Fact]
    public void CreateLinuxSearchEngine_DirectCall_ShouldNotThrow()
    {
        if (!OperatingSystem.IsLinux()) return;

        var act = () => { using var e = UnixSearchEngine.CreateLinuxSearchEngine(); };
        act.Should().NotThrow();
    }
}
