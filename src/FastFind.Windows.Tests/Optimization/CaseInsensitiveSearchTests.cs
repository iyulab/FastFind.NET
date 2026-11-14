using System.Diagnostics;
using System.Runtime.Versioning;
using FastFind.Models;
using FastFind.Windows.Implementation;
using FastFind.Windows.Tests.Helpers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FastFind.Windows.Tests.Optimization;

/// <summary>
/// What a case-insensitive text query means, asserted against the runtime's own ordinal-ignore-case search
/// rather than against how the match is implemented. The query evaluator and <see cref="FastFileItem"/>'s
/// <c>Matches*</c> methods answer with <see cref="StringComparison.OrdinalIgnoreCase"/>; they once answered
/// with <c>SIMDStringMatcher</c>, measured 3.9x slower on this path, and these tests are what keeps a custom
/// fold from coming back unnoticed.
/// </summary>
[Trait("Category", "Optimization")]
[Trait("Suite", "CaseInsensitiveSearch")]
[SupportedOSPlatform("windows")]
public class CaseInsensitiveSearchTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WindowsSearchIndex _searchIndex;
    private readonly List<FastFileItem> _files = GenerateTestFiles(5_000).ToList();

    public CaseInsensitiveSearchTests(ITestOutputHelper output)
    {
        _output = output;
        _searchIndex = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
    }

    public async Task InitializeAsync() => await _searchIndex.AddBatchAsync(_files);

    public async Task DisposeAsync() => await _searchIndex.DisposeAsync();

    [Theory]
    [InlineData("Document_00001.txt", "document", true)]
    [InlineData("Document_00001.txt", "DOCUMENT", true)]
    [InlineData("Document_00001.txt", "ument_0", true)]
    [InlineData("Document_00001.txt", "docs", false)]
    [InlineData("Document_00001.txt", "", true)]
    [InlineData("ab", "abc", false)]
    [InlineData("R\u00E9sum\u00E9.pdf", "R\u00C9SUM\u00C9", true)]
    [InlineData("\u041F\u0440\u0438\u0432\u0435\u0442.txt", "\u041F\u0420\u0418\u0412\u0415\u0422", true)]
    [InlineData("\u0391\u0392\u0393.txt", "\u03B1\u03B2\u03B3", true)]
    [InlineData("\u00C5.txt", "\u00E5", true)]
    // A full case mapping is not an ordinal one: "ß" is not "SS".
    [InlineData("Stra\u00DFe.txt", "STRASSE", false)]
    // The five compatibility characters on which the old per-character fold and the runtime's ordinal casing
    // disagree: none is a case variant of a letter in a file name, and the runtime's reading is the standard one.
    [InlineData("Kelvin.txt", "\u212Aelvin", false)]
    [InlineData("\u00C5.txt", "\u212B", false)]
    [InlineData("\u03A9.txt", "\u2126", false)]
    [InlineData("\u00DF.txt", "\u1E9E", false)]
    [InlineData("\u03F4.txt", "\u0398", false)]
    public void MatchesName_IsOrdinalIgnoreCase(string name, string term, bool expected)
    {
        var item = ItemNamed(name);

        item.MatchesName(term).Should().Be(expected);
        item.MatchesName(term.AsSpan()).Should().Be(expected);
        item.MatchesName(term).Should().Be(name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MatchesName_AgreesWithOrdinalIgnoreCase_ForEveryCharacterOfTheBasicPlane()
    {
        for (int i = 0; i <= 0xFFFF; i++)
        {
            if (i is >= 0xD800 and <= 0xDFFF) continue; // a surrogate half is not a character on its own

            var c = (char)i;
            var name = $"x{c}y.txt";
            var item = ItemNamed(name);

            foreach (var term in new[] { char.ToUpperInvariant(c).ToString(), char.ToLowerInvariant(c).ToString() })
            {
                item.MatchesName(term).Should().Be(name.Contains(term, StringComparison.OrdinalIgnoreCase),
                    $"U+{i:X4} searched as U+{(int)term[0]:X4}");
            }
        }
    }

    [Fact]
    public void MatchesPath_And_MatchesExtension_AreOrdinalIgnoreCase()
    {
        var item = ItemNamed("Report_00007.JSON");

        item.MatchesPath("TESTDATA\\folder7\\report").Should().BeTrue();
        item.MatchesPath("testdata/folder7").Should().BeFalse("a path is read as written; only case is folded");
        item.MatchesExtension("json").Should().BeTrue();
        item.MatchesExtension(".JS".AsSpan()).Should().BeTrue();
        item.MatchesExtension("xml").Should().BeFalse();
    }

    [Theory]
    [InlineData("document")]
    [InlineData("DOC")]
    [InlineData("_0000")]
    [InlineData("ab")]
    [InlineData("a")]
    [InlineData("PERFORMANCE_0004")]
    [InlineData("no such name")]
    public async Task Search_FileNameOnly_ReturnsExactlyTheCaseInsensitiveMatches(string text)
    {
        var query = new SearchQuery { SearchText = text, SearchFileNameOnly = true, CaseSensitive = false };

        var found = await SearchNamesAsync(query);
        var expected = _files.Where(f => f.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullPath).Order(StringComparer.Ordinal).ToList();

        _output.WriteLine($"\"{text}\": {found.Count:N0} of {_files.Count:N0}");
        found.Should().Equal(expected);
    }

    [Theory]
    [InlineData("folder12")]
    [InlineData("TESTDATA\\FOLDER9\\")]
    [InlineData("c:\\testdata")]
    public async Task Search_ByPath_ReturnsExactlyTheCaseInsensitiveMatches(string text)
    {
        var query = new SearchQuery { SearchText = text, CaseSensitive = false };

        var found = await SearchNamesAsync(query);
        var expected = _files.Where(f => f.FullPath.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullPath).Order(StringComparer.Ordinal).ToList();

        _output.WriteLine($"\"{text}\": {found.Count:N0} of {_files.Count:N0}");
        found.Should().Equal(expected);
    }

    [Fact]
    public async Task Search_CaseSensitive_DistinguishesCase()
    {
        var sensitive = await SearchNamesAsync(new SearchQuery { SearchText = "Document", SearchFileNameOnly = true, CaseSensitive = true });
        var wrongCase = await SearchNamesAsync(new SearchQuery { SearchText = "document", SearchFileNameOnly = true, CaseSensitive = true });
        var insensitive = await SearchNamesAsync(new SearchQuery { SearchText = "document", SearchFileNameOnly = true, CaseSensitive = false });

        sensitive.Should().NotBeEmpty();
        wrongCase.Should().BeEmpty("every generated name spells it with a capital");
        insensitive.Should().Equal(sensitive);
    }

    [PerformanceTestFact]
    [Trait("Category", "Performance")]
    public async Task Search_TwentyThousandFiles_CompletesQuickly()
    {
        var largeIndex = new WindowsSearchIndex(NullLogger<WindowsSearchIndex>.Instance);
        await largeIndex.AddBatchAsync(GenerateTestFiles(20_000));
        var query = new SearchQuery { SearchText = "performance", CaseSensitive = false };

        await foreach (var _ in largeIndex.SearchAsync(query)) { } // warm up

        const int iterations = 5;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await foreach (var _ in largeIndex.SearchAsync(query)) { }
        }
        sw.Stop();
        var average = sw.ElapsedMilliseconds / (double)iterations;

        _output.WriteLine($"{largeIndex.Count:N0} files, \"{query.SearchText}\": {average:F2} ms on average");
        await largeIndex.DisposeAsync();

        average.Should().BeLessThan(500, "a text query over 20,000 files should answer well inside half a second");
    }

    private async Task<List<string>> SearchNamesAsync(SearchQuery query)
    {
        var found = new List<string>();
        await foreach (var item in _searchIndex.SearchAsync(query))
        {
            found.Add(item.FullPath);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static FastFileItem ItemNamed(string name)
    {
        var folder = @"C:\TestData\Folder7";
        var extension = Path.GetExtension(name);
        return new FastFileItem(
            fullPath: $@"{folder}\{name}",
            name: name,
            directoryPath: folder,
            extension: extension,
            size: 1,
            created: DateTime.Now,
            modified: DateTime.Now,
            accessed: DateTime.Now,
            attributes: FileAttributes.Normal,
            driveLetter: 'C');
    }

    /// <summary>
    /// Generates test files with various naming patterns
    /// </summary>
    private static IEnumerable<FastFileItem> GenerateTestFiles(int count)
    {
        var random = new Random(42);
        var prefixes = new[] { "Document", "Report", "Data", "Config", "Performance", "Test", "Output" };
        var extensions = new[] { ".txt", ".cs", ".json", ".xml", ".log", ".md" };
        var now = DateTime.Now;

        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[i % prefixes.Length];
            var ext = extensions[i % extensions.Length];
            var fileName = $"{prefix}_{i:D5}{ext}";
            var folder = $@"C:\TestData\Folder{i % 100}";
            var fullPath = $@"{folder}\{fileName}";

            yield return new FastFileItem(
                fullPath: fullPath,
                name: fileName,
                directoryPath: folder,
                extension: ext,
                size: random.Next(100, 100000),
                created: now.AddDays(-random.Next(365)),
                modified: now.AddDays(-random.Next(30)),
                accessed: now,
                attributes: FileAttributes.Normal,
                driveLetter: 'C'
            );
        }
    }
}
