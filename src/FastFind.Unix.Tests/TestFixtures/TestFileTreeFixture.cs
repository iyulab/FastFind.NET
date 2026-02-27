namespace FastFind.Unix.Tests.TestFixtures;

public class TestFileTreeFixture : IDisposable
{
    public string RootPath { get; }

    public TestFileTreeFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"fastfind-test-{Guid.NewGuid():N}");
        CreateTestTree();
    }

    private void CreateTestTree()
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllBytes(Path.Combine(RootPath, "file1.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(RootPath, "file2.cs"), new byte[200]);
        File.WriteAllBytes(Path.Combine(RootPath, ".hidden"), new byte[50]);

        var sub1 = Path.Combine(RootPath, "sub1");
        Directory.CreateDirectory(sub1);
        File.WriteAllBytes(Path.Combine(sub1, "file3.txt"), new byte[150]);

        var sub1a = Path.Combine(sub1, "sub1a");
        Directory.CreateDirectory(sub1a);
        File.WriteAllBytes(Path.Combine(sub1a, "file4.log"), new byte[300]);

        var sub2 = Path.Combine(RootPath, "sub2");
        Directory.CreateDirectory(sub2);
        File.WriteAllBytes(Path.Combine(sub2, "file5.pdf"), new byte[500]);
    }

    public void Dispose()
    {
        try { Directory.Delete(RootPath, true); } catch { }
    }
}
