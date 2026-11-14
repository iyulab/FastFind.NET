using FastFind.Interfaces;
using AwesomeAssertions;

namespace FastFind.Unix.Tests.Linux;

[Trait("Category", "Integration")]
[Trait("OS", "Linux")]
public class FactoryRegistrationTests
{
    [LinuxFact]
    public void EnsureRegistered_OnLinux_ShouldRegisterFactory()
    {
        UnixRegistration.EnsureRegistered();
        var platforms = FastFinder.GetAvailablePlatforms();
        platforms.Should().Contain(PlatformType.Linux);
    }

    [LinuxFact]
    public void CreateSearchEngine_OnLinux_ShouldReturnEngine()
    {
        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();
        engine.Should().NotBeNull();
    }

    [LinuxFact]
    public void CreateLinuxSearchEngine_DirectCall_ShouldNotThrow()
    {
        var act = () => { using var e = UnixSearchEngine.CreateLinuxSearchEngine(); };
        act.Should().NotThrow();
    }
}
