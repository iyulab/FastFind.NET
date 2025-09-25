using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FastFind.Models;

namespace FastFind.Extensions;

/// <summary>
/// Multilingual search extensions for FastFind.NET supporting all UTF-8 languages
/// FastFind.NET용 모든 UTF-8 언어를 지원하는 다국어 검색 확장 기능
/// </summary>
public static class MultilingualSearchExtensions
{
    // Unicode ranges for major language groups
    private static readonly Dictionary<string, (int Start, int End)> UnicodeRanges = new()
    {
        // East Asian Languages
        {"Korean", (0xAC00, 0xD7A3)},           // Hangul Syllables
        {"KoreanJamo", (0x3130, 0x318F)},       // Hangul Compatibility Jamo
        {"Chinese", (0x4E00, 0x9FFF)},          // CJK Unified Ideographs
        {"Japanese", (0x3040, 0x309F)},         // Hiragana
        {"JapaneseKatakana", (0x30A0, 0x30FF)}, // Katakana

        // Arabic and Hebrew
        {"Arabic", (0x0600, 0x06FF)},           // Arabic
        {"Hebrew", (0x0590, 0x05FF)},           // Hebrew

        // Cyrillic (Russian, Bulgarian, Serbian, etc.)
        {"Cyrillic", (0x0400, 0x04FF)},         // Cyrillic

        // Greek
        {"Greek", (0x0370, 0x03FF)},            // Greek and Coptic

        // Thai and other Southeast Asian
        {"Thai", (0x0E00, 0x0E7F)},             // Thai
        {"Vietnamese", (0x1EA0, 0x1EFF)},       // Vietnamese Extended

        // Devanagari (Hindi, Sanskrit, etc.)
        {"Devanagari", (0x0900, 0x097F)},       // Devanagari

        // Latin Extended (European languages)
        {"LatinExtended", (0x0100, 0x017F)},    // Latin Extended-A
        {"LatinExtended2", (0x0180, 0x024F)},   // Latin Extended-B
    };

    /// <summary>
    /// Detect languages present in text using Unicode ranges
    /// Unicode 범위를 사용하여 텍스트에 포함된 언어 감지
    /// </summary>
    public static MultilingualTextStatistics AnalyzeTextLanguages(this string text)
    {
        if (string.IsNullOrEmpty(text))
            return new MultilingualTextStatistics();

        var stats = new MultilingualTextStatistics
        {
            TotalCharacters = text.Length,
            OriginalText = text
        };

        foreach (char c in text)
        {
            var codePoint = (int)c;
            bool classified = false;

            // Check against all Unicode ranges
            foreach (var (language, (start, end)) in UnicodeRanges)
            {
                if (codePoint >= start && codePoint <= end)
                {
                    if (!stats.DetectedLanguages.ContainsKey(language))
                        stats.DetectedLanguages[language] = 0;
                    stats.DetectedLanguages[language]++;
                    classified = true;
                    break;
                }
            }

            // Basic ASCII/Latin classification
            if (!classified)
            {
                if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z')
                {
                    stats.LatinCharacters++;
                }
                else if (char.IsDigit(c))
                {
                    stats.Numbers++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    stats.Spaces++;
                }
                else if (char.IsPunctuation(c) || char.IsSymbol(c))
                {
                    stats.Punctuation++;
                }
            }
        }

        // Determine primary language
        stats.PrimaryLanguage = stats.DetectedLanguages
            .OrderByDescending(kvp => kvp.Value)
            .FirstOrDefault().Key ?? "Latin";

        stats.IsMultilingual = stats.DetectedLanguages.Count > 1;
        stats.RequiresUnicodeNormalization = stats.DetectedLanguages.Any();

        return stats;
    }

    /// <summary>
    /// Prepare search text for optimal multilingual matching
    /// 다국어 매칭을 위한 검색 텍스트 최적화 준비
    /// </summary>
    public static string PrepareMultilingualSearchText(this string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return string.Empty;

        var prepared = searchText;

        // Unicode normalization - critical for multilingual text
        prepared = prepared.Normalize(NormalizationForm.FormC);

        // Trim and normalize whitespace
        prepared = prepared.Trim();
        prepared = Regex.Replace(prepared, @"\s+", " ");

        // Remove zero-width characters and BOM
        prepared = prepared.Replace("\u200B", ""); // Zero-width space
        prepared = prepared.Replace("\u200C", ""); // Zero-width non-joiner
        prepared = prepared.Replace("\u200D", ""); // Zero-width joiner
        prepared = prepared.Replace("\uFEFF", ""); // Byte order mark
        prepared = prepared.Replace("\u00AD", ""); // Soft hyphen

        return prepared;
    }

    /// <summary>
    /// Generate search patterns for multilingual text
    /// 다국어 텍스트를 위한 검색 패턴 생성
    /// </summary>
    public static IEnumerable<string> GenerateMultilingualSearchPatterns(this string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var stats = text.AnalyzeTextLanguages();
        var prepared = text.PrepareMultilingualSearchText();

        // Original text (normalized)
        yield return prepared;

        // Different Unicode normalization forms for compatibility
        yield return text.Normalize(NormalizationForm.FormD);
        yield return text.Normalize(NormalizationForm.FormKC);
        yield return text.Normalize(NormalizationForm.FormKD);

        // Wildcard patterns for partial matching
        if (prepared.Length > 1)
        {
            yield return $"*{prepared}*";
            yield return $"{prepared}*";
            yield return $"*{prepared}";
        }

        // Language-specific patterns
        switch (stats.PrimaryLanguage)
        {
            case "Korean":
                foreach (var pattern in GenerateKoreanPatterns(prepared))
                    yield return pattern;
                break;

            case "Chinese":
                foreach (var pattern in GenerateChinesePatterns(prepared))
                    yield return pattern;
                break;

            case "Japanese":
                foreach (var pattern in GenerateJapanesePatterns(prepared))
                    yield return pattern;
                break;

            case "Arabic":
                foreach (var pattern in GenerateArabicPatterns(prepared))
                    yield return pattern;
                break;

            default:
                foreach (var pattern in GenerateLatinPatterns(prepared))
                    yield return pattern;
                break;
        }

        // Common variations for all languages
        if (prepared.Contains(' '))
        {
            yield return prepared.Replace(" ", "");
            yield return prepared.Replace(" ", "_");
            yield return prepared.Replace(" ", "-");
            yield return prepared.Replace(" ", ".");
        }

        if (prepared.Contains('_'))
        {
            yield return prepared.Replace("_", " ");
            yield return prepared.Replace("_", "-");
            yield return prepared.Replace("_", ".");
        }

        if (prepared.Contains('-'))
        {
            yield return prepared.Replace("-", " ");
            yield return prepared.Replace("-", "_");
            yield return prepared.Replace("-", ".");
        }
    }

    /// <summary>
    /// Advanced fuzzy matching for multilingual text
    /// 다국어 텍스트를 위한 고급 퍼지 매칭
    /// </summary>
    public static bool MultilingualFuzzyMatch(this string fileName, string searchText, double threshold = 0.7)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(searchText))
            return false;

        var fileStats = fileName.AnalyzeTextLanguages();
        var searchStats = searchText.AnalyzeTextLanguages();

        // If languages don't match, lower the threshold
        var adjustedThreshold = fileStats.PrimaryLanguage == searchStats.PrimaryLanguage
            ? threshold
            : threshold * 0.8;

        // Normalize both strings
        var normalizedFileName = fileName.PrepareMultilingualSearchText().ToLowerInvariant();
        var normalizedSearchText = searchText.PrepareMultilingualSearchText().ToLowerInvariant();

        // Direct substring match
        if (normalizedFileName.Contains(normalizedSearchText))
            return true;

        // Try different Unicode normalization forms
        var forms = new[] { NormalizationForm.FormC, NormalizationForm.FormD,
                           NormalizationForm.FormKC, NormalizationForm.FormKD };

        foreach (var form in forms)
        {
            var fileNorm = fileName.Normalize(form).ToLowerInvariant();
            var searchNorm = searchText.Normalize(form).ToLowerInvariant();

            if (fileNorm.Contains(searchNorm))
                return true;
        }

        // Language-specific fuzzy matching
        return fileStats.PrimaryLanguage switch
        {
            "Korean" => KoreanSpecificMatch(normalizedFileName, normalizedSearchText),
            "Chinese" => ChineseSpecificMatch(normalizedFileName, normalizedSearchText),
            "Japanese" => JapaneseSpecificMatch(normalizedFileName, normalizedSearchText),
            "Arabic" => ArabicSpecificMatch(normalizedFileName, normalizedSearchText),
            _ => LatinSpecificMatch(normalizedFileName, normalizedSearchText, adjustedThreshold)
        };
    }

    /// <summary>
    /// Create optimized search query for multilingual text
    /// 다국어 텍스트를 위한 최적화된 검색 쿼리 생성
    /// </summary>
    public static SearchQuery CreateOptimizedMultilingualQuery(this string searchText,
        IList<string>? searchLocations = null)
    {
        var stats = searchText.AnalyzeTextLanguages();
        var preparedText = searchText.PrepareMultilingualSearchText();

        var query = new SearchQuery
        {
            SearchText = preparedText,
            CaseSensitive = false,
            UseRegex = false,
            SearchFileNameOnly = true,
            IncludeFiles = true,
            IncludeDirectories = true
        };

        if (searchLocations != null && searchLocations.Any())
        {
            query.SearchLocations = searchLocations;
        }

        // Optimize based on detected languages
        if (stats.IsMultilingual)
        {
            query.MaxResults = 2000; // More results for multilingual queries
        }
        else if (stats.PrimaryLanguage == "Korean" || stats.PrimaryLanguage == "Chinese" ||
                 stats.PrimaryLanguage == "Japanese")
        {
            query.MaxResults = 1500; // More results for CJK languages
        }
        else
        {
            query.MaxResults = 1000; // Standard results for Latin-based languages
        }

        // Special handling for RTL languages
        if (stats.PrimaryLanguage == "Arabic" || stats.PrimaryLanguage == "Hebrew")
        {
            query.MaxResults = 1200;
        }

        return query;
    }

    #region Language-specific pattern generators

    private static IEnumerable<string> GenerateKoreanPatterns(string text)
    {
        // Existing Korean logic from KoreanSearchExtensions
        if (text.Contains(' '))
        {
            yield return text.Replace(" ", "");
            yield return text.Replace(" ", "_");
        }
    }

    private static IEnumerable<string> GenerateChinesePatterns(string text)
    {
        // Chinese-specific patterns
        if (text.Contains(' '))
        {
            yield return text.Replace(" ", "");
        }

        // Traditional/Simplified variations could be added here
    }

    private static IEnumerable<string> GenerateJapanesePatterns(string text)
    {
        // Japanese-specific patterns
        if (text.Contains(' '))
        {
            yield return text.Replace(" ", "");
        }

        // Hiragana/Katakana variations could be added here
    }

    private static IEnumerable<string> GenerateArabicPatterns(string text)
    {
        // Arabic-specific patterns - handle RTL
        if (text.Contains(' '))
        {
            yield return text.Replace(" ", "");
        }
    }

    private static IEnumerable<string> GenerateLatinPatterns(string text)
    {
        // Latin-based language patterns
        if (text.Contains(' '))
        {
            yield return text.Replace(" ", "");
            yield return text.Replace(" ", "_");
            yield return text.Replace(" ", "-");
        }

        // Accent variations for European languages
        var unaccented = text.RemoveAccents();
        if (unaccented != text)
        {
            yield return unaccented;
        }
    }

    #endregion

    #region Language-specific matching

    private static bool KoreanSpecificMatch(string fileName, string searchText)
    {
        // Use existing Korean matching logic
        var variations = new[]
        {
            searchText.Replace(" ", ""),
            searchText.Replace(" ", "_"),
            searchText.Replace(" ", "-"),
            searchText.Replace("_", " "),
            searchText.Replace("-", " ")
        };

        return variations.Any(variation => fileName.Contains(variation));
    }

    private static bool ChineseSpecificMatch(string fileName, string searchText)
    {
        // Chinese-specific matching
        return fileName.Contains(searchText.Replace(" ", ""));
    }

    private static bool JapaneseSpecificMatch(string fileName, string searchText)
    {
        // Japanese-specific matching
        return fileName.Contains(searchText.Replace(" ", ""));
    }

    private static bool ArabicSpecificMatch(string fileName, string searchText)
    {
        // Arabic-specific matching (RTL considerations)
        return fileName.Contains(searchText.Replace(" ", ""));
    }

    private static bool LatinSpecificMatch(string fileName, string searchText, double threshold)
    {
        // Enhanced Latin matching with accent removal
        var fileNameUnaccented = fileName.RemoveAccents();
        var searchTextUnaccented = searchText.RemoveAccents();

        if (fileNameUnaccented.Contains(searchTextUnaccented))
            return true;

        // Levenshtein distance for fuzzy matching
        var distance = ComputeLevenshteinDistance(fileName, searchText);
        var similarity = 1.0 - (double)distance / Math.Max(fileName.Length, searchText.Length);

        return similarity >= threshold;
    }

    #endregion

    #region Helper methods

    /// <summary>
    /// Remove accents from Latin-based text
    /// 라틴 기반 텍스트에서 악센트 제거
    /// </summary>
    private static string RemoveAccents(this string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

        foreach (char c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder
            .ToString()
            .Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Compute Levenshtein distance for fuzzy matching
    /// 퍼지 매칭을 위한 편집 거리 계산
    /// </summary>
    private static int ComputeLevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s))
            return string.IsNullOrEmpty(t) ? 0 : t.Length;

        if (string.IsNullOrEmpty(t))
            return s.Length;

        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    #endregion
}

/// <summary>
/// Multilingual text statistics for search optimization
/// 검색 최적화를 위한 다국어 텍스트 통계
/// </summary>
public class MultilingualTextStatistics
{
    /// <summary>Total number of characters</summary>
    public int TotalCharacters { get; set; }

    /// <summary>Original text being analyzed</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>Detected languages with character counts</summary>
    public Dictionary<string, int> DetectedLanguages { get; set; } = new();

    /// <summary>Number of Latin/ASCII characters</summary>
    public int LatinCharacters { get; set; }

    /// <summary>Number of space characters</summary>
    public int Spaces { get; set; }

    /// <summary>Number of numeric characters</summary>
    public int Numbers { get; set; }

    /// <summary>Number of punctuation and symbol characters</summary>
    public int Punctuation { get; set; }

    /// <summary>Primary detected language</summary>
    public string PrimaryLanguage { get; set; } = "Latin";

    /// <summary>Whether text contains multiple languages</summary>
    public bool IsMultilingual { get; set; }

    /// <summary>Whether text requires Unicode normalization</summary>
    public bool RequiresUnicodeNormalization { get; set; }

    /// <summary>
    /// Get ratio of non-Latin characters (0.0 to 1.0)
    /// </summary>
    public double NonLatinRatio => TotalCharacters > 0 ?
        (double)DetectedLanguages.Values.Sum() / TotalCharacters : 0.0;

    /// <summary>
    /// Get complexity score for search optimization
    /// </summary>
    public double ComplexityScore
    {
        get
        {
            double score = 0.0;

            // Multilingual text is more complex
            if (IsMultilingual) score += 0.3;

            // Non-Latin languages add complexity
            score += NonLatinRatio * 0.4;

            // More languages = more complexity
            score += Math.Min(DetectedLanguages.Count * 0.1, 0.3);

            return Math.Min(score, 1.0);
        }
    }

    /// <summary>
    /// Get a comprehensive summary of the text characteristics
    /// </summary>
    public string GetSummary()
    {
        var parts = new List<string>();

        foreach (var (language, count) in DetectedLanguages.OrderByDescending(kvp => kvp.Value))
        {
            parts.Add($"{count} {language}");
        }

        if (LatinCharacters > 0)
            parts.Add($"{LatinCharacters} Latin");

        if (Numbers > 0)
            parts.Add($"{Numbers} digits");

        if (Spaces > 0)
            parts.Add($"{Spaces} spaces");

        var summary = string.Join(", ", parts);

        if (IsMultilingual)
            summary += " (Multilingual)";
        else if (PrimaryLanguage != "Latin")
            summary += $" ({PrimaryLanguage} primary)";

        return summary.Length > 0 ? summary : "Empty text";
    }
}