using FastFind.Windows.Mft;
using Xunit;

namespace FastFind.Windows.Tests.Helpers;

/// <summary>
/// A test that needs the change-journal provider, which needs an administrator on NTFS.
/// </summary>
/// <remarks>
/// It is reported as <b>skipped</b>, with the reason, where the provider is unavailable. A test that
/// returns early instead reports a pass for a run in which it checked nothing — which is how the
/// exclusion check on this provider went unrun on every CI runner without anyone seeing it.
/// </remarks>
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        if (!MftFileSystemProvider.IsMftAccessAvailable)
        {
            Skip = "Needs the NTFS change-journal provider, which needs administrator rights.";
        }
    }
}
