using FastFind;
using FastFind.Interfaces;
using FastFind.Unix.Linux;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace FastFind.Unix;

/// <summary>
/// Unix/Linux search engine registration helper
/// </summary>
public static class UnixRegistration
{
    private static volatile bool _isRegistered = false;
    private static readonly object _lock = new object();

    /// <summary>
    /// Module initializer that automatically registers the Unix search engine factory
    /// when the FastFind.Unix assembly is loaded. This ensures that users don't need
    /// to manually call EnsureRegistered() before using FastFinder.CreateUnixSearchEngine().
    /// </summary>
    /// <remarks>
    /// CA2255 is suppressed because this library intentionally uses ModuleInitializer
    /// to provide transparent factory registration when the assembly is loaded.
    /// </remarks>
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        EnsureRegistered();
    }

    /// <summary>
    /// Ensures the Unix search engine factory is registered
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_isRegistered) return;

        lock (_lock)
        {
            if (_isRegistered) return;

            if (OperatingSystem.IsLinux())
            {
                FastFinder.RegisterSearchEngineFactory(
                    PlatformType.Linux,
                    loggerFactory => UnixSearchEngine.CreateLinuxSearchEngine(loggerFactory));
                _isRegistered = true;
            }

            if (OperatingSystem.IsMacOS())
            {
                FastFinder.RegisterSearchEngineFactory(
                    PlatformType.MacOS,
                    loggerFactory => UnixSearchEngine.CreateMacOSSearchEngine(loggerFactory));
                _isRegistered = true;
            }
        }
    }
}
