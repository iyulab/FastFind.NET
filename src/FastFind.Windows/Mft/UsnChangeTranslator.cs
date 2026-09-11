using System.Runtime.Versioning;
using FastFind.Models;
using Microsoft.Win32.SafeHandles;

namespace FastFind.Windows.Mft;

/// <summary>
/// Where a change record's parent directory is on disk.
/// </summary>
internal interface IDirectoryPathSource
{
    /// <summary>The path of the directory with this file reference on this volume, if it still exists.</summary>
    bool TryGetPath(char driveLetter, ulong fileReference, out string path);

    /// <summary>
    /// A directory was renamed, moved or deleted. Any remembered path at or beneath it may now be
    /// wrong.
    /// </summary>
    void Invalidate(char driveLetter);
}

/// <summary>
/// Turns change-journal records into the file change events an index applies.
/// </summary>
/// <remarks>
/// <para>
/// A journal logs several records per operation: one each time the set of reasons grows while the
/// file is open, and a final one carrying <c>CLOSE</c> with every reason accumulated. Creations,
/// modifications and deletions are therefore reported once, from the closing record. A rename logs
/// the old name and parent first, then the new ones; the two are paired into one
/// <see cref="FileChangeType.Renamed"/> carrying both paths, so the index can drop the old entry.
/// </para>
/// <para>
/// A record names only its entry and its parent's file reference, so each one is placed by asking
/// <see cref="IDirectoryPathSource"/> where that parent is now. A record whose parent no longer
/// exists yields nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class UsnChangeTranslator(IDirectoryPathSource directories)
{
    private readonly Dictionary<(char Drive, ulong Reference), string> _renamedFrom = new();

    public FileChangeEventArgs? Translate(in UsnChangeRecord record)
    {
        var reason = record.Reason;
        var key = (record.DriveLetter, record.FileReferenceNumber);
        var closing = (reason & UsnReason.Close) != 0;

        if (record.IsDirectory && (reason & (UsnReason.RenameOldName | UsnReason.RenameNewName | UsnReason.FileDelete)) != 0)
        {
            // The old-name record is placed by its old parent, which still exists; paths cached
            // beneath the moved directory are stale from here on.
            directories.Invalidate(record.DriveLetter);
        }

        if (!TryGetPath(record, out var path))
        {
            if (closing) _renamedFrom.Remove(key);
            return null;
        }

        if ((reason & UsnReason.RenameOldName) != 0)
        {
            _renamedFrom[key] = path;
            return null;
        }

        if ((reason & UsnReason.RenameNewName) != 0 && _renamedFrom.Remove(key, out var oldPath))
        {
            return new FileChangeEventArgs(FileChangeType.Renamed, path, oldPath: oldPath);
        }

        if (!closing)
            return null;

        if ((reason & UsnReason.FileDelete) != 0)
            return new FileChangeEventArgs(FileChangeType.Deleted, path);

        return new FileChangeEventArgs(
            (reason & UsnReason.FileCreate) != 0 ? FileChangeType.Created : FileChangeType.Modified,
            path);
    }

    private bool TryGetPath(in UsnChangeRecord record, out string path)
    {
        if (directories.TryGetPath(record.DriveLetter, record.ParentFileReferenceNumber, out var parent))
        {
            path = Path.Join(parent, record.FileName);
            return true;
        }

        path = string.Empty;
        return false;
    }
}

/// <summary>
/// Finds a directory from its file reference by opening it by id and asking Windows for its final
/// path — the platform's own answer, which needs no copy of the volume's directory tree.
/// </summary>
/// <remarks>
/// Resolved paths are remembered per volume, up to a bound, until a directory is renamed, moved or
/// deleted on that volume. One handle per volume, to the root directory, serves as the volume hint
/// <c>OpenFileById</c> requires.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class FileIdDirectoryPaths : IDirectoryPathSource, IDisposable
{
    private const int Capacity = 16_384;

    private readonly Dictionary<char, SafeFileHandle> _volumeHints = new();
    private readonly Dictionary<(char Drive, ulong Reference), string> _paths = new();

    public bool TryGetPath(char driveLetter, ulong fileReference, out string path)
    {
        if (_paths.TryGetValue((driveLetter, fileReference), out path!))
            return true;

        var hint = GetVolumeHint(driveLetter);
        if (hint is null || !TryGetFinalPath(hint, fileReference, out path))
            return false;

        if (_paths.Count >= Capacity) _paths.Clear();
        _paths[(driveLetter, fileReference)] = path;
        return true;
    }

    public void Invalidate(char driveLetter)
    {
        foreach (var key in _paths.Keys.Where(k => k.Drive == driveLetter).ToList())
            _paths.Remove(key);
    }

    private SafeFileHandle? GetVolumeHint(char driveLetter)
    {
        if (_volumeHints.TryGetValue(driveLetter, out var handle))
            return handle;

        handle = NativeMethods.CreateFileW(
            $"{driveLetter}:\\",
            NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        _volumeHints[driveLetter] = handle;
        return handle;
    }

    private static unsafe bool TryGetFinalPath(SafeFileHandle volumeHint, ulong fileReference, out string path)
    {
        path = string.Empty;
        var descriptor = FILE_ID_DESCRIPTOR.ForFileReference(fileReference);

        using var handle = NativeMethods.OpenFileById(
            volumeHint,
            descriptor,
            NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS);

        if (handle.IsInvalid)
            return false;

        Span<char> buffer = stackalloc char[512];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = NativeMethods.GetFinalPathNameByHandleW(handle, pointer, (uint)buffer.Length,
                NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);
        }

        if (length == 0)
            return false;

        if (length >= buffer.Length)
        {
            var larger = new char[length + 1];
            fixed (char* pointer = larger)
            {
                length = NativeMethods.GetFinalPathNameByHandleW(handle, pointer, (uint)larger.Length,
                    NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);
            }
            if (length == 0 || length >= larger.Length) return false;
            path = StripExtendedPrefix(new string(larger, 0, (int)length));
            return true;
        }

        path = StripExtendedPrefix(new string(buffer[..(int)length]));
        return true;
    }

    /// <summary>
    /// <c>GetFinalPathNameByHandleW</c> returns <c>\\?\C:\dir</c>; the index stores <c>C:\dir</c>.
    /// </summary>
    internal static string StripExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && path[5] == ':'
            ? path[4..]
            : path;

    public void Dispose()
    {
        foreach (var handle in _volumeHints.Values) handle.Dispose();
        _volumeHints.Clear();
        _paths.Clear();
    }
}
