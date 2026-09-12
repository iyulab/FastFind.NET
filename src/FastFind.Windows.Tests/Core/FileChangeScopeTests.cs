using FastFind.Models;
using FluentAssertions;
using Xunit;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// What a change looks like from inside a boundary — a set of monitored locations, or the paths an
/// exclusion list leaves out.
/// </summary>
/// <remarks>
/// A rename is the case that matters: one that leaves the boundary is a deletion from inside it, and
/// one that arrives is a creation. Without that fold, a file renamed into an excluded directory is
/// dropped and the index keeps the path it no longer has.
/// </remarks>
[Trait("Category", "Functional")]
public class FileChangeScopeTests
{
    private static bool Inside(string path) => path.StartsWith(@"C:\watched", StringComparison.OrdinalIgnoreCase);

    private static FileChangeEventArgs? Restrict(FileChangeEventArgs change) =>
        FileChangeScope.Restrict(change, Inside);

    [Fact]
    public void A_Change_Inside_The_Boundary_Should_Pass_Through()
    {
        var change = new FileChangeEventArgs(FileChangeType.Created, @"C:\watched\a.txt");

        Restrict(change).Should().BeSameAs(change);
    }

    [Fact]
    public void A_Change_Outside_The_Boundary_Should_Be_Dropped()
    {
        var change = new FileChangeEventArgs(FileChangeType.Created, @"C:\elsewhere\a.txt");

        Restrict(change).Should().BeNull();
    }

    [Fact]
    public void A_Rename_Within_The_Boundary_Should_Pass_Through()
    {
        var change = new FileChangeEventArgs(
            FileChangeType.Renamed, @"C:\watched\b.txt", oldPath: @"C:\watched\a.txt");

        Restrict(change).Should().BeSameAs(change);
    }

    [Fact]
    public void A_Rename_Out_Of_The_Boundary_Should_Become_A_Deletion_Of_The_Old_Path()
    {
        var change = new FileChangeEventArgs(
            FileChangeType.Renamed, @"C:\elsewhere\a.txt", oldPath: @"C:\watched\a.txt");

        var scoped = Restrict(change);

        scoped.Should().NotBeNull();
        scoped!.ChangeType.Should().Be(FileChangeType.Deleted);
        scoped.NewPath.Should().Be(@"C:\watched\a.txt", "the path the index holds is the one that is gone");
    }

    [Fact]
    public void A_Rename_Into_The_Boundary_Should_Become_A_Creation_Of_The_New_Path()
    {
        var change = new FileChangeEventArgs(
            FileChangeType.Renamed, @"C:\watched\a.txt", oldPath: @"C:\elsewhere\a.txt");

        var scoped = Restrict(change);

        scoped.Should().NotBeNull();
        scoped!.ChangeType.Should().Be(FileChangeType.Created);
        scoped.NewPath.Should().Be(@"C:\watched\a.txt");
    }

    [Fact]
    public void A_Rename_Entirely_Outside_The_Boundary_Should_Be_Dropped()
    {
        var change = new FileChangeEventArgs(
            FileChangeType.Renamed, @"C:\elsewhere\b.txt", oldPath: @"C:\elsewhere\a.txt");

        Restrict(change).Should().BeNull();
    }

    [Fact]
    public void A_Null_Change_Should_Stay_Null()
    {
        FileChangeScope.Restrict(null, Inside).Should().BeNull();
    }
}
