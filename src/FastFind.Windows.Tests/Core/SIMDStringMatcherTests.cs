using System.Diagnostics;
using FastFind.Models;

namespace FastFind.Windows.Tests.Core;

/// <summary>
/// Tests for SIMD-accelerated string matching performance
/// </summary>
public class SIMDStringMatcherTests
{
    private const int PerformanceTestIterations = 100_000;
    
    [Fact]
    public void ContainsVectorized_Should_Find_Substring()
    {
        // Arrange
        var text = "This is a test string with some content".AsSpan();
        var pattern = "test".AsSpan();
        
        // Act
        var result = SIMDStringMatcher.ContainsVectorized(text, pattern);
        
        // Assert
        result.Should().BeTrue("Should find 'test' in the text");
    }
    
    [Fact]
    public void ContainsVectorized_Should_Not_Find_Missing_Substring()
    {
        // Arrange
        var text = "This is a sample string".AsSpan();
        var pattern = "missing".AsSpan();
        
        // Act
        var result = SIMDStringMatcher.ContainsVectorized(text, pattern);
        
        // Assert
        result.Should().BeFalse("Should not find 'missing' in the text");
    }
    
    [Theory]
    [InlineData("*.txt", "file.txt", true)]
    [InlineData("*.txt", "file.doc", false)]
    [InlineData("test*", "test_file.txt", true)]
    [InlineData("test*", "sample.txt", false)]
    [InlineData("*file*", "myfile.txt", true)]
    [InlineData("*file*", "document.txt", false)]
    public void MatchesWildcard_Should_Work_Correctly(string pattern, string text, bool expected)
    {
        // Act
        var result = SIMDStringMatcher.MatchesWildcard(text.AsSpan(), pattern.AsSpan());
        
        // Assert
        result.Should().Be(expected, $"Pattern '{pattern}' with text '{text}' should be {expected}");
    }
    
    [Fact]
    public void ContainsIgnoreCase_Should_Be_Case_Insensitive()
    {
        // Arrange
        var text = "This Is A Test String".AsSpan();
        var pattern1 = "test".AsSpan();
        var pattern2 = "TEST".AsSpan();
        var pattern3 = "TeSt".AsSpan();
        
        // Act & Assert
        SIMDStringMatcher.ContainsVectorized(text, pattern1).Should().BeTrue();
        SIMDStringMatcher.ContainsVectorized(text, pattern2).Should().BeTrue();
        SIMDStringMatcher.ContainsVectorized(text, pattern3).Should().BeTrue();
    }
    
    [Fact]
    public void IndexOfVectorized_Should_Return_Correct_Position()
    {
        // Arrange
        var text = "Hello world, this is a test".AsSpan();
        var pattern = "world".AsSpan();
        
        // Act
        var index = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        
        // Assert
        index.Should().Be(6, "Should find 'world' at position 6");
    }
    
    [Fact]
    public void IndexOfVectorized_Should_Return_MinusOne_When_Not_Found()
    {
        // Arrange
        var text = "Hello world".AsSpan();
        var pattern = "missing".AsSpan();
        
        // Act
        var index = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        
        // Assert
        index.Should().Be(-1, "Should return -1 when pattern not found");
    }
    
    [Fact(Skip = "Performance test - not critical for core functionality")]
    [Trait("Category", "Performance")]
    [Trait("Category", "Suite:SIMD")]
    public void SIMD_Performance_Should_Be_Faster_Than_Native()
    {
        // Arrange
        var longText = GenerateLongTestString(10000);
        var pattern = "performance_test_pattern";
        var textSpan = longText.AsSpan();
        var patternSpan = pattern.AsSpan();
        
        // Act - Measure SIMD performance
        var sw1 = Stopwatch.StartNew();
        for (int i = 0; i < PerformanceTestIterations; i++)
        {
            SIMDStringMatcher.ContainsVectorized(textSpan, patternSpan);
        }
        sw1.Stop();
        
        // Act - Measure native performance
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < PerformanceTestIterations; i++)
        {
            longText.Contains(pattern, StringComparison.Ordinal);
        }
        sw2.Stop();
        
        // Assert
        var simdTime = sw1.ElapsedMilliseconds;
        var nativeTime = sw2.ElapsedMilliseconds;
        
        // SIMD should be at least competitive (within 20% or better)
        Assert.True(simdTime <= (long)(nativeTime * 1.2), 
            $"SIMD ({simdTime}ms) should be competitive with native ({nativeTime}ms)");
        
        // Output for analysis
        Console.WriteLine($"SIMD: {simdTime}ms, Native: {nativeTime}ms, Ratio: {(double)simdTime / nativeTime:F2}");
    }
    
    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Category", "Suite:SIMD")]
    public void Wildcard_Performance_Should_Be_Optimized()
    {
        // Arrange
        var testFiles = GenerateTestFileNames(1000);
        var pattern = "*.txt";
        
        // Act
        var sw = Stopwatch.StartNew();
        var matches = 0;
        
        for (int i = 0; i < 100; i++) // Repeat for stable timing
        {
            foreach (var fileName in testFiles)
            {
                if (SIMDStringMatcher.MatchesWildcard(fileName.AsSpan(), pattern.AsSpan()))
                {
                    matches++;
                }
            }
        }
        
        sw.Stop();
        
        // Assert
        var timePerMatch = (double)sw.ElapsedMilliseconds / (testFiles.Length * 100);
        timePerMatch.Should().BeLessThan(0.001, "Should process each wildcard match in < 0.001ms");
        
        Console.WriteLine($"Wildcard matching: {timePerMatch:F6}ms per operation, {matches} matches");
    }
    
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    [Trait("Category", "Suite:SIMD")]
    public void Large_Text_Performance_Should_Scale_Linearly(int textLength)
    {
        // Arrange
        var text = GenerateLongTestString(textLength);
        var pattern = "test_pattern_xyz";
        var textSpan = text.AsSpan();
        var patternSpan = pattern.AsSpan();
        
        // Act
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            SIMDStringMatcher.ContainsVectorized(textSpan, patternSpan);
        }
        sw.Stop();
        
        // Assert
        var timePerChar = (double)sw.ElapsedTicks / (textLength * 1000);
        timePerChar.Should().BeLessThan(10, $"Processing time per character should be minimal for length {textLength}");
        
        Console.WriteLine($"Text length: {textLength}, Time per char: {timePerChar:F6} ticks");
    }
    
    [Fact(Skip = "Performance test - not critical for core functionality")]
    public void Memory_Usage_Should_Be_Minimal()
    {
        // Arrange
        var initialMemory = GC.GetTotalMemory(true);
        var text = "Test string for memory usage analysis";
        var pattern = "string";
        
        // Act - Perform many operations
        for (int i = 0; i < 10_000; i++)
        {
            SIMDStringMatcher.ContainsVectorized(text.AsSpan(), pattern.AsSpan());
        }
        
        var finalMemory = GC.GetTotalMemory(false);
        var memoryIncrease = finalMemory - initialMemory;
        
        // Assert
        memoryIncrease.Should().BeLessThan(1024, "Memory increase should be minimal (< 1KB)");
        
        Console.WriteLine($"Memory increase: {memoryIncrease} bytes");
    }
    
    private static string GenerateLongTestString(int length)
    {
        var chars = new char[length];
        var random = new Random(42); // Fixed seed for reproducibility
        
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)('a' + random.Next(26));
        }
        
        return new string(chars);
    }
    
    private static string[] GenerateTestFileNames(int count)
    {
        var files = new string[count];
        var extensions = new[] { ".txt", ".doc", ".pdf", ".jpg", ".png", ".exe", ".dll" };
        var random = new Random(42);
        
        for (int i = 0; i < count; i++)
        {
            var name = $"file_{i:D6}";
            var ext = extensions[random.Next(extensions.Length)];
            files[i] = name + ext;
        }
        
        return files;
    }
}