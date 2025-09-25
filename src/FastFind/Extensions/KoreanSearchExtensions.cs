using System.Text;
using System.Text.RegularExpressions;
using FastFind.Models;

namespace FastFind.Extensions;

/// <summary>
/// Korean language search extensions for FastFind.NET
/// FastFind.NET용 한국어 검색 확장 기능
/// </summary>
public static class KoreanSearchExtensions
{
    // Hangul Unicode ranges
    private const int HANGUL_SYLLABLES_START = 0xAC00; // 가
    private const int HANGUL_SYLLABLES_END = 0xD7A3;   // 힣
    private const int HANGUL_JAMO_START = 0x3130;      // ㄱ
    private const int HANGUL_JAMO_END = 0x318F;        // ㆎ
    private const int HANGUL_COMPATIBILITY_JAMO_START = 0x3200; // ㈀
    private const int HANGUL_COMPATIBILITY_JAMO_END = 0x32FF;   // ㋿

    /// <summary>
    /// Check if text contains Korean characters
    /// 텍스트에 한글 문자가 포함되어 있는지 확인
    /// </summary>
    public static bool ContainsKorean(this string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        return text.Any(c =>
            (c >= HANGUL_SYLLABLES_START && c <= HANGUL_SYLLABLES_END) ||
            (c >= HANGUL_JAMO_START && c <= HANGUL_JAMO_END) ||
            (c >= HANGUL_COMPATIBILITY_JAMO_START && c <= HANGUL_COMPATIBILITY_JAMO_END));
    }

    /// <summary>
    /// Create Korean-optimized search query
    /// 한글 최적화 검색 쿼리 생성
    /// </summary>
    public static SearchQuery OptimizeForKorean(this SearchQuery query)
    {
        if (string.IsNullOrEmpty(query.SearchText))
            return query;

        // If search text contains Korean, optimize for Korean search
        if (query.SearchText.ContainsKorean())
        {
            query.CaseSensitive = false; // Korean is case-insensitive by nature
            query.SearchFileNameOnly = true; // Focus on file names for better performance

            // Ensure proper Unicode normalization for Korean text
            query.SearchText = query.SearchText.Normalize(NormalizationForm.FormC);
        }

        return query;
    }

    /// <summary>
    /// Create multiple search patterns for Korean text to improve matching
    /// 한글 텍스트의 매칭을 개선하기 위한 다중 검색 패턴 생성
    /// </summary>
    public static IEnumerable<string> GenerateKoreanSearchPatterns(this string koreanText)
    {
        if (string.IsNullOrEmpty(koreanText))
            yield break;

        // Original text
        yield return koreanText;

        // Normalized forms
        yield return koreanText.Normalize(NormalizationForm.FormC);
        yield return koreanText.Normalize(NormalizationForm.FormD);

        // With wildcards for partial matching
        if (koreanText.Length > 1)
        {
            yield return $"*{koreanText}*";
            yield return $"{koreanText}*";
            yield return $"*{koreanText}";
        }

        // Space variations (common in Korean file names)
        if (koreanText.Contains(' '))
        {
            yield return koreanText.Replace(" ", "");
            yield return koreanText.Replace(" ", "_");
            yield return koreanText.Replace(" ", "-");
        }
    }

    /// <summary>
    /// Check if Korean search text matches Korean file name with fuzzy matching
    /// 퍼지 매칭을 통한 한글 검색어와 한글 파일명 일치 확인
    /// </summary>
    public static bool KoreanFuzzyMatch(this string fileName, string searchText)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(searchText))
            return false;

        // Normalize both strings for comparison
        var normalizedFileName = fileName.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var normalizedSearchText = searchText.Normalize(NormalizationForm.FormC).ToLowerInvariant();

        // Direct match
        if (normalizedFileName.Contains(normalizedSearchText))
            return true;

        // Try space variations
        var variations = new[]
        {
            normalizedSearchText.Replace(" ", ""),
            normalizedSearchText.Replace(" ", "_"),
            normalizedSearchText.Replace(" ", "-"),
            normalizedSearchText.Replace("_", " "),
            normalizedSearchText.Replace("-", " ")
        };

        return variations.Any(variation => normalizedFileName.Contains(variation));
    }

    /// <summary>
    /// Get Korean character statistics for search optimization
    /// 검색 최적화를 위한 한글 문자 통계 가져오기
    /// </summary>
    public static KoreanTextStatistics GetKoreanStatistics(this string text)
    {
        if (string.IsNullOrEmpty(text))
            return new KoreanTextStatistics();

        var stats = new KoreanTextStatistics
        {
            TotalCharacters = text.Length,
            HangulSyllables = text.Count(c => c >= HANGUL_SYLLABLES_START && c <= HANGUL_SYLLABLES_END),
            HangulJamo = text.Count(c => c >= HANGUL_JAMO_START && c <= HANGUL_JAMO_END),
            Spaces = text.Count(c => c == ' '),
            Numbers = text.Count(char.IsDigit),
            English = text.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
        };

        stats.IsKoreanPrimary = stats.HangulSyllables > stats.English;
        stats.IsMixed = stats.HangulSyllables > 0 && stats.English > 0;

        return stats;
    }

    /// <summary>
    /// Create optimized search query based on Korean text analysis
    /// 한글 텍스트 분석을 기반으로 최적화된 검색 쿼리 생성
    /// </summary>
    public static SearchQuery CreateOptimizedKoreanQuery(this string searchText,
        IList<string>? searchLocations = null)
    {
        var stats = searchText.GetKoreanStatistics();

        var query = new SearchQuery
        {
            SearchText = searchText.Normalize(NormalizationForm.FormC),
            CaseSensitive = false,
            UseRegex = false,
            SearchFileNameOnly = true,
            IncludeFiles = true,
            IncludeDirectories = true,
            MaxResults = stats.IsKoreanPrimary ? 1000 : 500 // More results for Korean queries
        };

        if (searchLocations != null && searchLocations.Any())
        {
            query.SearchLocations = searchLocations;
        }

        // Optimize based on text characteristics
        if (stats.IsMixed)
        {
            // Mixed Korean-English: be more permissive
            query.MaxResults = 1500;
        }

        if (stats.Spaces > 0)
        {
            // Contains spaces: might need fuzzy matching
            query.MaxResults = 800;
        }

        return query;
    }

    /// <summary>
    /// Validate and prepare Korean search text for optimal performance
    /// 최적 성능을 위한 한글 검색 텍스트 검증 및 준비
    /// </summary>
    public static string PrepareKoreanSearchText(this string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return string.Empty;

        // Normalize Unicode form
        var normalized = searchText.Normalize(NormalizationForm.FormC);

        // Trim whitespace
        normalized = normalized.Trim();

        // Replace multiple spaces with single space
        normalized = Regex.Replace(normalized, @"\s+", " ");

        // Remove zero-width characters that might interfere with search
        normalized = normalized.Replace("\u200B", ""); // Zero-width space
        normalized = normalized.Replace("\uFEFF", ""); // Byte order mark

        return normalized;
    }
}

/// <summary>
/// Korean text statistics for search optimization
/// 검색 최적화를 위한 한글 텍스트 통계
/// </summary>
public class KoreanTextStatistics
{
    /// <summary>Total number of characters</summary>
    public int TotalCharacters { get; set; }

    /// <summary>Number of Hangul syllables (가-힣)</summary>
    public int HangulSyllables { get; set; }

    /// <summary>Number of Hangul Jamo (ㄱ-ㅎ, ㅏ-ㅣ)</summary>
    public int HangulJamo { get; set; }

    /// <summary>Number of space characters</summary>
    public int Spaces { get; set; }

    /// <summary>Number of English characters</summary>
    public int English { get; set; }

    /// <summary>Number of numeric characters</summary>
    public int Numbers { get; set; }

    /// <summary>Whether Korean is the primary language in the text</summary>
    public bool IsKoreanPrimary { get; set; }

    /// <summary>Whether text contains both Korean and English</summary>
    public bool IsMixed { get; set; }

    /// <summary>
    /// Get Korean ratio (0.0 to 1.0)
    /// </summary>
    public double KoreanRatio => TotalCharacters > 0 ? (double)HangulSyllables / TotalCharacters : 0.0;

    /// <summary>
    /// Get a summary of the text characteristics
    /// </summary>
    public string GetSummary()
    {
        var parts = new List<string>();

        if (HangulSyllables > 0)
            parts.Add($"{HangulSyllables} Korean chars");

        if (English > 0)
            parts.Add($"{English} English chars");

        if (Numbers > 0)
            parts.Add($"{Numbers} numbers");

        if (Spaces > 0)
            parts.Add($"{Spaces} spaces");

        var summary = string.Join(", ", parts);

        if (IsKoreanPrimary)
            summary += " (Korean primary)";
        else if (IsMixed)
            summary += " (Mixed language)";

        return summary.Length > 0 ? summary : "Empty text";
    }
}