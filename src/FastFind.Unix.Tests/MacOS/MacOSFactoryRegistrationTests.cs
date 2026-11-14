using FastFind.Interfaces;
using AwesomeAssertions;

namespace FastFind.Unix.Tests.MacOS;

[Trait("Category", "Integration")]
[Trait("OS", "macOS")]
public class MacOSFactoryRegistrationTests
{
    [MacOSFact]
    public void EnsureRegistered_OnMacOS_ShouldRegisterFactory()
    {
        UnixRegistration.EnsureRegistered();
        var platforms = FastFinder.GetAvailablePlatforms();
        platforms.Should().Contain(PlatformType.MacOS);
    }

    [MacOSFact]
    public void CreateSearchEngine_OnMacOS_ShouldReturnEngine()
    {
        UnixRegistration.EnsureRegistered();
        using var engine = FastFinder.CreateSearchEngine();
        engine.Should().NotBeNull();
    }

    [MacOSFact]
    public void CreateMacOSSearchEngine_DirectCall_ShouldNotThrow()
    {
        var act = () => { using var e = UnixSearchEngine.CreateMacOSSearchEngine(); };
        act.Should().NotThrow();
    }
}
