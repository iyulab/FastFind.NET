namespace FastFind.Unix.Common;

/// <summary>
/// Helper utilities for Unix path and file system operations
/// </summary>
internal static class UnixPathHelper
{
    /// <summary>
    /// Virtual/pseudo file system types that should be excluded from indexing
    /// </summary>
    private static readonly HashSet<string> VirtualFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysfs",
        "proc",
        "tmpfs",
        "devtmpfs",
        "devpts",
        "securityfs",
        "cgroup",
        "cgroup2",
        "pstore",
        "debugfs",
        "hugetlbfs",
        "mqueue",
        "fusectl",
        "configfs",
        "binfmt_misc",
        "autofs",
        "efivarfs",
        "tracefs",
        "bpf",
        "ramfs",
        "rpc_pipefs",
        "nsfs",
        "overlay"
    };

    /// <summary>
    /// Determines whether the given file system type is a virtual/pseudo file system
    /// that should be excluded from indexing and search operations.
    /// </summary>
    /// <param name="fsType">The file system type string (e.g., "ext4", "proc", "tmpfs")</param>
    /// <returns>True if the file system is virtual and should be excluded</returns>
    public static bool IsVirtualFileSystem(string fsType)
    {
        if (string.IsNullOrWhiteSpace(fsType))
            return true;

        return VirtualFileSystems.Contains(fsType.Trim());
    }

    /// <summary>
    /// Gets the mount point identifier for Unix systems.
    /// On Unix, the root mount point '/' serves as the universal drive identifier.
    /// </summary>
    /// <returns>The root mount point character '/'</returns>
    public static char GetMountPointIdentifier()
    {
        return '/';
    }
}
