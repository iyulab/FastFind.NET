using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Numerics;

namespace FastFind.Models;

/// <summary>
/// Ultra-high performance SIMD-optimized string matching
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

        // 짧은 문자열은 기본 구현 사용
        if (needle.Length < 4 || !Avx2.IsSupported)
        {
            var scalarResult = ContainsScalar(haystack, needle);
            StringMatchingStats.RecordScalarSearch();
            return scalarResult;
        }

        var simdResult = ContainsAvx2(haystack, needle);
        StringMatchingStats.RecordSIMDSearch();
        return simdResult;
    }

    /// <summary>
    /// AVX2 기반 초고속 문자열 검색
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe bool ContainsAvx2(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        var needleFirst = char.ToLowerInvariant(needle[0]);

        // 16개 문자를 동시에 비교
        var firstVector = Vector256.Create((short)needleFirst);
        var toLowerMask = Vector256.Create((short)0x20); // 소문자 변환 마스크
        var upperBound = Vector256.Create((short)'Z');
        var lowerBound = Vector256.Create((short)'A');

        fixed (char* haystackPtr = haystack)
        {
            var haystackShorts = (short*)haystackPtr;
            var maxIndex = haystack.Length - needle.Length;
            var vectorSize = Vector256<short>.Count; // 16

            // 벡터 단위로 처리
            for (int i = 0; i <= maxIndex - vectorSize + 1; i += vectorSize)
            {
                // 16개 문자 로드
                var chunk = Avx2.LoadVector256(haystackShorts + i);

                // 대소문자 구분 없는 비교를 위한 소문자 변환
                var isUpper = Avx2.And(
                    Avx2.CompareGreaterThan(chunk, lowerBound.AsInt16()),
                    Avx2.CompareGreaterThan(upperBound.AsInt16(), chunk)
                );
                var lowerChunk = Avx2.BlendVariable(chunk, Avx2.Or(chunk, toLowerMask), isUpper);

                // 첫 번째 문자 매치 검사
                var firstMatches = Avx2.CompareEqual(lowerChunk, firstVector);

                if (!Avx2.TestZ(firstMatches, firstMatches))
                {
                    // 매치된 위치들에서 전체 문자열 검사
                    var matchMask = Avx2.MoveMask(firstMatches.AsByte());

                    while (matchMask != 0)
                    {
                        var matchIndex = BitOperations.TrailingZeroCount((uint)matchMask) / 2;
                        var actualIndex = i + matchIndex;

                        if (actualIndex <= maxIndex && MatchesAtPosition(haystack, needle, actualIndex))
                        {
                            return true;
                        }

                        // 다음 매치 위치로
                        matchMask &= matchMask - 1;
                    }
                }
            }

            // 남은 부분을 스칼라로 처리
            var remainingStart = (maxIndex / vectorSize) * vectorSize;
            return ContainsScalar(haystack.Slice(remainingStart), needle);
        }
    }

    /// <summary>
    /// 스칼라 버전 (폴백)
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
    /// 특정 위치에서 대소문자 구분 없이 매치 확인
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesAtPosition(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle, int position)
    {
        var haystackSlice = haystack.Slice(position, needle.Length);

        // 4문자씩 배치로 비교 (64비트 정수 비교)
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

                // 64비트 정수로 한번에 비교
                var haystackQuad = ((ulong)h4 << 48) | ((ulong)h3 << 32) | ((ulong)h2 << 16) | h1;
                var needleQuad = ((ulong)n4 << 48) | ((ulong)n3 << 32) | ((ulong)n2 << 16) | n1;

                if (haystackQuad != needleQuad)
                    return false;
            }
        }

        // 남은 문자들 처리
        for (int i = (needle.Length / 4) * 4; i < needle.Length; i++)
        {
            if (char.ToLowerInvariant(haystackSlice[i]) != char.ToLowerInvariant(needle[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 와일드카드 패턴 매칭 (*, ? 지원)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MatchesWildcard(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        return MatchesWildcardRecursive(text, pattern, 0, 0);
    }

    private static bool MatchesWildcardRecursive(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern, int textIndex, int patternIndex)
    {
        // 패턴 끝에 도달
        if (patternIndex >= pattern.Length)
            return textIndex >= text.Length;

        // 텍스트 끝에 도달했지만 패턴이 남음
        if (textIndex >= text.Length)
            return pattern.Slice(patternIndex).IndexOfAnyExcept('*') < 0;

        var patternChar = pattern[patternIndex];
        var textChar = text[textIndex];

        // 와일드카드 처리
        if (patternChar == '*')
        {
            // 연속된 * 건너뛰기
            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                patternIndex++;

            if (patternIndex >= pattern.Length)
                return true; // 패턴이 *로 끝남

            // * 다음 패턴을 찾아서 매치
            for (int i = textIndex; i < text.Length; i++)
            {
                if (MatchesWildcardRecursive(text, pattern, i, patternIndex))
                    return true;
            }
            return false;
        }

        // 단일 문자 와일드카드 또는 정확한 매치
        if (patternChar == '?' || char.ToLowerInvariant(patternChar) == char.ToLowerInvariant(textChar))
        {
            return MatchesWildcardRecursive(text, pattern, textIndex + 1, patternIndex + 1);
        }

        return false;
    }
}

/// <summary>
/// 성능 모니터링 및 통계
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