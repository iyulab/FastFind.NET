using FastFind.Interfaces;
using FastFind.Unix.Common;
using Microsoft.Extensions.Logging;

namespace FastFind.Unix.Linux;

/// <summary>
/// Linux-optimized file system provider using standard .NET APIs
/// with Channel-based architecture for high-throughput async processing.
/// Reads its mount points from /proc/mounts; everything else is <see cref="UnixFileSystemProvider"/>.
/// </summary>
internal class LinuxFileSystemProvider : UnixFileSystemProvider
{
    public LinuxFileSystemProvider(ILoggerFactory? loggerFactory = null) : base(loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override PlatformType SupportedPlatform => PlatformType.Linux;

    /// <inheritdoc/>
    public override bool IsAvailable => OperatingSystem.IsLinux();

    /// <inheritdoc/>
    protected override int ExpectedFilesPerSecond => 50_000;

    /// <inheritdoc/>
    public override Task<IEnumerable<Interfaces.DriveInfo>> GetAvailableLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var drives = new List<Interfaces.DriveInfo>();

        try
        {
            var mounts = ParseProcMounts();

            foreach (var mount in mounts)
            {
                if (UnixPathHelper.IsVirtualFileSystem(mount.FsType))
                    continue;

                try
                {
                    var sysDrive = new System.IO.DriveInfo(mount.MountPoint);
                    if (sysDrive.IsReady)
                    {
                        drives.Add(new Interfaces.DriveInfo
                        {
                            Name = mount.MountPoint,
                            Label = mount.Device,
                            FileSystem = mount.FsType,
                            TotalSize = sysDrive.TotalSize,
                            AvailableSpace = sysDrive.AvailableFreeSpace,
                            IsReady = true,
                            DriveType = MapDriveType(sysDrive.DriveType)
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Error reading drive info for mount: {Mount}", mount.MountPoint);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error parsing /proc/mounts");
        }

        // Fallback: ensure at least root is included
        if (drives.Count == 0)
        {
            try
            {
                var rootDrive = new System.IO.DriveInfo("/");
                drives.Add(new Interfaces.DriveInfo
                {
                    Name = "/",
                    Label = "Root",
                    FileSystem = "unknown",
                    TotalSize = rootDrive.IsReady ? rootDrive.TotalSize : 0,
                    AvailableSpace = rootDrive.IsReady ? rootDrive.AvailableFreeSpace : 0,
                    IsReady = rootDrive.IsReady,
                    DriveType = Interfaces.DriveType.Fixed
                });
            }
            catch
            {
                drives.Add(new Interfaces.DriveInfo
                {
                    Name = "/",
                    Label = "Root",
                    FileSystem = "unknown",
                    TotalSize = 0,
                    AvailableSpace = 0,
                    IsReady = true,
                    DriveType = Interfaces.DriveType.Fixed
                });
            }
        }

        return Task.FromResult<IEnumerable<Interfaces.DriveInfo>>(drives);
    }

    /// <inheritdoc/>
    public override Task<string> GetFileSystemTypeAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            var mounts = ParseProcMounts();
            var fullPath = Path.GetFullPath(path);

            // Find the longest matching mount point (physical FS first, then any FS)
            MountEntry? bestMatch = null;
            MountEntry? fallbackMatch = null;
            int bestLength = -1;
            int fallbackLength = -1;

            foreach (var mount in mounts)
            {
                if (!fullPath.StartsWith(mount.MountPoint, StringComparison.Ordinal))
                    continue;

                if (UnixPathHelper.IsVirtualFileSystem(mount.FsType))
                {
                    // Track as fallback (e.g., overlay in Docker containers)
                    if (mount.MountPoint.Length > fallbackLength)
                    {
                        fallbackMatch = mount;
                        fallbackLength = mount.MountPoint.Length;
                    }
                    continue;
                }

                if (mount.MountPoint.Length > bestLength)
                {
                    bestMatch = mount;
                    bestLength = mount.MountPoint.Length;
                }
            }

            if (bestMatch != null)
            {
                return Task.FromResult(bestMatch.FsType);
            }

            // Fallback to virtual FS type (e.g., overlay in containers)
            if (fallbackMatch != null)
            {
                return Task.FromResult(fallbackMatch.FsType);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error determining file system type for: {Path}", path);
        }

        return Task.FromResult("unknown");
    }

    // ────────────────────────────── Private Helpers ──────────────────────────────

    /// <summary>
    /// Parses /proc/mounts to get mount point information.
    /// </summary>
    private static List<MountEntry> ParseProcMounts()
    {
        var mounts = new List<MountEntry>();
        const string procMountsPath = "/proc/mounts";

        if (!File.Exists(procMountsPath))
            return mounts;

        try
        {
            foreach (var line in File.ReadLines(procMountsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Format: device mountpoint fstype options dump pass
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    mounts.Add(new MountEntry
                    {
                        Device = parts[0],
                        MountPoint = parts[1],
                        FsType = parts[2]
                    });
                }
            }
        }
        catch
        {
            // Cannot read /proc/mounts — return empty
        }

        return mounts;
    }


    /// <summary>
    /// Represents a parsed mount entry from /proc/mounts
    /// </summary>
    private class MountEntry
    {
        public string Device { get; set; } = string.Empty;
        public string MountPoint { get; set; } = string.Empty;
        public string FsType { get; set; } = string.Empty;
    }
}
