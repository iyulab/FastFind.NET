using Xunit;

namespace FastFind.Unix.Tests;

/// <summary>A test that needs a Linux host: reported as skipped, with the reason, anywhere else.</summary>
/// <remarks>
/// A test that begins with <c>if (!OperatingSystem.IsLinux()) return;</c> reports a pass on every other host for a
/// run that checked nothing; a skip says so.
/// </remarks>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Needs a Linux host.";
    }
}

/// <summary>A test that needs a macOS host: reported as skipped, with the reason, anywhere else.</summary>
public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Needs a macOS host.";
    }
}

/// <summary>A test of POSIX semantics (a backslash is an ordinary file name character, case is significant): skipped on Windows.</summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "POSIX semantics; not meaningful on Windows.";
    }
}
