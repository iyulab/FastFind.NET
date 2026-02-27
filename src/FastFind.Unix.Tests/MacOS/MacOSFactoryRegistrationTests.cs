using FastFind.Interfaces;
using FluentAssertions;

namespace FastFind.Unix.Tests.MacOS;

[Trait("Category", "Integration")]
[Trait("OS", "macOS")]
public class MacOSFactoryRegistrationTests
{
    [Fact]
    public void EnsureRegistered_OnMacOS_ShouldRegisterFactory()
    {
        if (!OperatingSystem.IsMacOS()) return;

        UnixRegistration.EnsureRegistered();
        var platforms = FastFinder.GetAvailablePlatforms();
        platforms.Should().Contain(PlatformType.MacOS);
    }

    [Fact]
    public void CreateSearchEngine_OnMacOS_ShouldReturnEngine()
    {
        if (!OperatingSystem.IsMacOS()) return;

        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();
        engine.Should().NotBeNull();
    }

    [Fact]
    public void CreateMacOSSearchEngine_DirectCall_ShouldNotThrow()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var act = () => { using var e = UnixSearchEngine.CreateMacOSSearchEngine(); };
        act.Should().NotThrow();
    }
}
