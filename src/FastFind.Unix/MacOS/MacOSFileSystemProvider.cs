using FastFind.Interfaces;
using FastFind.Unix.Common;
using Microsoft.Extensions.Logging;

namespace FastFind.Unix.MacOS;

/// <summary>
/// macOS-optimized file system provider using standard .NET APIs
/// with Channel-based architecture for high-throughput async processing.
/// Uses DriveInfo and /Volumes enumeration instead of /proc/mounts; everything else is <see cref="UnixFileSystemProvider"/>.
/// </summary>
internal class MacOSFileSystemProvider : UnixFileSystemProvider
{
    public MacOSFileSystemProvider(ILoggerFactory? loggerFactory = null) : base(loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override PlatformType SupportedPlatform => PlatformType.MacOS;

    /// <inheritdoc/>
    public override bool IsAvailable => OperatingSystem.IsMacOS();

    /// <inheritdoc/>
    protected override int ExpectedFilesPerSecond => 40_000;

    /// <inheritdoc/>
    public override Task<IEnumerable<Interfaces.DriveInfo>> GetAvailableLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var drives = new List<Interfaces.DriveInfo>();
        var knownMountPoints = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            // Primary: Use DriveInfo.GetDrives() for all ready, non-virtual drives
            foreach (var sysDrive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (!sysDrive.IsReady)
                        continue;

                    var fsType = sysDrive.DriveFormat;
                    if (UnixPathHelper.IsVirtualFileSystem(fsType))
                        continue;

                    knownMountPoints.Add(sysDrive.RootDirectory.FullName);

                    drives.Add(new Interfaces.DriveInfo
                    {
                        Name = sysDrive.RootDirectory.FullName,
                        Label = sysDrive.VolumeLabel,
                        FileSystem = fsType,
                        TotalSize = sysDrive.TotalSize,
                        AvailableSpace = sysDrive.AvailableFreeSpace,
                        IsReady = true,
                        DriveType = MapDriveType(sysDrive.DriveType)
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Error reading drive info for: {Drive}", sysDrive.Name);
                }
            }

            // Supplement: Enumerate /Volumes directory for additional mount points
            const string volumesPath = "/Volumes";
            if (Directory.Exists(volumesPath))
            {
                try
                {
                    foreach (var volumeDir in Directory.EnumerateDirectories(volumesPath))
                    {
                        if (knownMountPoints.Contains(volumeDir))
                            continue;

                        try
                        {
                            var volDrive = new System.IO.DriveInfo(volumeDir);
                            if (!volDrive.IsReady)
                                continue;

                            var fsType = volDrive.DriveFormat;
                            if (UnixPathHelper.IsVirtualFileSystem(fsType))
                                continue;

                            knownMountPoints.Add(volumeDir);

                            drives.Add(new Interfaces.DriveInfo
                            {
                                Name = volumeDir,
                                Label = volDrive.VolumeLabel,
                                FileSystem = fsType,
                                TotalSize = volDrive.TotalSize,
                                AvailableSpace = volDrive.AvailableFreeSpace,
                                IsReady = true,
                                DriveType = MapDriveType(volDrive.DriveType)
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "Error reading volume info: {Volume}", volumeDir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Error enumerating /Volumes directory");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error enumerating drives");
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
                    Label = "Macintosh HD",
                    FileSystem = "apfs",
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
                    Label = "Macintosh HD",
                    FileSystem = "apfs",
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
            var fullPath = Path.GetFullPath(path);

            // Find the longest matching root path from available drives
            System.IO.DriveInfo? bestMatch = null;
            int bestLength = -1;

            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                        continue;

                    var rootPath = drive.RootDirectory.FullName;
                    if (!fullPath.StartsWith(rootPath, StringComparison.Ordinal))
                        continue;

                    if (rootPath.Length > bestLength)
                    {
                        bestMatch = drive;
                        bestLength = rootPath.Length;
                    }
                }
                catch
                {
                    // Skip inaccessible drives
                }
            }

            if (bestMatch != null)
            {
                return Task.FromResult(bestMatch.DriveFormat);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error determining file system type for: {Path}", path);
        }

        return Task.FromResult("unknown");
    }
}
