using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Numerics;

namespace FastFind.Models;

/// <summary>
/// Ultra-high performance SIMD-optimized string matching
/// Uses cross-platform Vector256/Vector128 abstractions for portability across x86 (AVX2/SSE2) and ARM (NEON).
/// </summary>
public static class SIMDStringMatcher
{
    /// <summary>
    /// SIMD-optimized case-insensitive string contains check
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsVectorized(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;

        // Short needles use scalar implementation
        if (needle.Length < 4)
        {
            var scalarResult = ContainsScalar(haystack, needle);
            StringMatchingStats.RecordScalarSearch();
            return scalarResult;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            // Vector256 path (works on AVX2 x86 AND 2×NEON ARM64)
            var simdResult = ContainsVector256(haystack, needle);
            StringMatchingStats.RecordSIMDSearch();
            return simdResult;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            // Vector128 path (works on SSE2 x86 AND NEON ARM64)
            var simdResult = ContainsVector128(haystack, needle);
            StringMatchingStats.RecordSIMDSearch();
            return simdResult;
        }

        // Scalar fallback
        var fallbackResult = ContainsScalar(haystack, needle);
        StringMatchingStats.RecordScalarSearch();
        return fallbackResult;
    }

    /// <summary>
    /// Cross-platform Vector256-based high-speed string search (processes 16 chars at a time)
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe bool ContainsVector256(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        var needleFirst = char.ToLowerInvariant(needle[0]);

        // Compare 16 characters simultaneously
        var firstVector = Vector256.Create((short)needleFirst);
        var toLowerMask = Vector256.Create((short)0x20); // lowercase conversion mask
        var upperBound = Vector256.Create((short)'Z');
        var lowerBound = Vector256.Create((short)'A');

        fixed (char* haystackPtr = haystack)
        {
            var haystackShorts = (short*)haystackPtr;
            var maxIndex = haystack.Length - needle.Length;
            var vectorSize = Vector256<short>.Count; // 16

            // Process in vector-sized chunks
            for (int i = 0; i <= maxIndex - vectorSize + 1; i += vectorSize)
            {
                // Load 16 characters
                var chunk = Vector256.Load(haystackShorts + i);

                // Case-insensitive comparison via lowercase conversion
                var isUpper = Vector256.GreaterThan(chunk, lowerBound) &
                              Vector256.GreaterThan(upperBound, chunk);
                var lowerChunk = Vector256.ConditionalSelect(isUpper, chunk | toLowerMask, chunk);

                // First character match check
                var firstMatches = Vector256.Equals(lowerChunk, firstVector);

                if (firstMatches != Vector256<short>.Zero)
                {
                    // Check full string at matched positions
                    var matchMask = firstMatches.AsByte().ExtractMostSignificantBits();

                    while (matchMask != 0)
                    {
                        var matchIndex = BitOperations.TrailingZeroCount(matchMask) / 2;
                        var actualIndex = i + matchIndex;

                        if (actualIndex <= maxIndex && MatchesAtPosition(haystack, needle, actualIndex))
                        {
                            return true;
                        }

                        // Move to next match position
                        matchMask &= matchMask - 1;
                    }
                }
            }

            // Process remaining elements with scalar fallback
            var remainingStart = (maxIndex / vectorSize) * vectorSize;
            return ContainsScalar(haystack.Slice(remainingStart), needle);
        }
    }

    /// <summary>
    /// Cross-platform Vector128-based string search (processes 8 chars at a time)
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe bool ContainsVector128(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        var needleFirst = char.ToLowerInvariant(needle[0]);

        // Compare 8 characters simultaneously
        var firstVector = Vector128.Create((short)needleFirst);
        var toLowerMask = Vector128.Create((short)0x20); // lowercase conversion mask
        var upperBound = Vector128.Create((short)'Z');
        var lowerBound = Vector128.Create((short)'A');

        fixed (char* haystackPtr = haystack)
        {
            var haystackShorts = (short*)haystackPtr;
            var maxIndex = haystack.Length - needle.Length;
            var vectorSize = Vector128<short>.Count; // 8

            // Process in vector-sized chunks
            for (int i = 0; i <= maxIndex - vectorSize + 1; i += vectorSize)
            {
                // Load 8 characters
                var chunk = Vector128.Load(haystackShorts + i);

                // Case-insensitive comparison via lowercase conversion
                var isUpper = Vector128.GreaterThan(chunk, lowerBound) &
                              Vector128.GreaterThan(upperBound, chunk);
                var lowerChunk = Vector128.ConditionalSelect(isUpper, chunk | toLowerMask, chunk);

                // First character match check
                var firstMatches = Vector128.Equals(lowerChunk, firstVector);

                if (firstMatches != Vector128<short>.Zero)
                {
                    // Check full string at matched positions
                    var matchMask = firstMatches.AsByte().ExtractMostSignificantBits();

                    while (matchMask != 0)
                    {
                        var matchIndex = BitOperations.TrailingZeroCount(matchMask) / 2;
                        var actualIndex = i + matchIndex;

                        if (actualIndex <= maxIndex && MatchesAtPosition(haystack, needle, actualIndex))
                        {
                            return true;
                        }

                        // Move to next match position
                        matchMask &= matchMask - 1;
                    }
                }
            }

            // Process remaining elements with scalar fallback
            var remainingStart = (maxIndex / vectorSize) * vectorSize;
            return ContainsScalar(haystack.Slice(remainingStart), needle);
        }
    }

    /// <summary>
    /// Scalar fallback
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsScalar(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (MatchesAtPosition(haystack, needle, i))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check case-insensitive match at specific position
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesAtPosition(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle, int position)
    {
        var haystackSlice = haystack.Slice(position, needle.Length);

        // Compare in batches of 4 characters (64-bit integer comparison)
        if (needle.Length >= 4)
        {
            for (int i = 0; i <= needle.Length - 4; i += 4)
            {
                var h1 = char.ToLowerInvariant(haystackSlice[i]);
                var h2 = char.ToLowerInvariant(haystackSlice[i + 1]);
                var h3 = char.ToLowerInvariant(haystackSlice[i + 2]);
                var h4 = char.ToLowerInvariant(haystackSlice[i + 3]);

                var n1 = char.ToLowerInvariant(needle[i]);
                var n2 = char.ToLowerInvariant(needle[i + 1]);
                var n3 = char.ToLowerInvariant(needle[i + 2]);
                var n4 = char.ToLowerInvariant(needle[i + 3]);

                // Compare as 64-bit integer in one operation
                var haystackQuad = ((ulong)h4 << 48) | ((ulong)h3 << 32) | ((ulong)h2 << 16) | h1;
                var needleQuad = ((ulong)n4 << 48) | ((ulong)n3 << 32) | ((ulong)n2 << 16) | n1;

                if (haystackQuad != needleQuad)
                    return false;
            }
        }

        // Handle remaining characters
        for (int i = (needle.Length / 4) * 4; i < needle.Length; i++)
        {
            if (char.ToLowerInvariant(haystackSlice[i]) != char.ToLowerInvariant(needle[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Wildcard pattern matching (supports *, ?)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesWildcard(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        return MatchesWildcardRecursive(text, pattern, 0, 0);
    }

    private static bool MatchesWildcardRecursive(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern, int textIndex, int patternIndex)
    {
        if (patternIndex >= pattern.Length)
            return textIndex >= text.Length;

        if (textIndex >= text.Length)
            return pattern.Slice(patternIndex).IndexOfAnyExcept('*') < 0;

        var patternChar = pattern[patternIndex];
        var textChar = text[textIndex];

        if (patternChar == '*')
        {
            // Skip consecutive *
            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                patternIndex++;

            if (patternIndex >= pattern.Length)
                return true; // Pattern ends with *

            // Find match for pattern after *
            for (int i = textIndex; i < text.Length; i++)
            {
                if (MatchesWildcardRecursive(text, pattern, i, patternIndex))
                    return true;
            }
            return false;
        }

        if (patternChar == '?' || char.ToLowerInvariant(patternChar) == char.ToLowerInvariant(textChar))
        {
            return MatchesWildcardRecursive(text, pattern, textIndex + 1, patternIndex + 1);
        }

        return false;
    }
}

/// <summary>
/// Performance monitoring and statistics
/// </summary>
public static class StringMatchingStats
{
    private static long _totalSearches = 0;
    private static long _simdSearches = 0;
    private static long _scalarSearches = 0;

    internal static void RecordSIMDSearch()
    {
        Interlocked.Increment(ref _simdSearches);
        Interlocked.Increment(ref _totalSearches);
    }

    internal static void RecordScalarSearch()
    {
        Interlocked.Increment(ref _scalarSearches);
        Interlocked.Increment(ref _totalSearches);
    }

    public static long TotalSearches => Interlocked.Read(ref _totalSearches);
    public static long SIMDSearches => Interlocked.Read(ref _simdSearches);
    public static long ScalarSearches => Interlocked.Read(ref _scalarSearches);
    public static double SIMDUsagePercentage => TotalSearches == 0 ? 0 : (SIMDSearches * 100.0) / TotalSearches;

    public static void Reset()
    {
        Interlocked.Exchange(ref _totalSearches, 0);
        Interlocked.Exchange(ref _simdSearches, 0);
        Interlocked.Exchange(ref _scalarSearches, 0);
    }
}
