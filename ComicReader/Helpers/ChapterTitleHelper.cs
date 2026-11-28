using System;
using System.Text.RegularExpressions;

namespace ComicReader.Helpers
{
    /// <summary>
    /// Extracts concise chapter labels (e.g., "Chương 10") from verbose folder or archive entry names
    /// such as "Vo Luyen Dinh Phong Chap 10 Next Chap 11 - NetTruyen".
    /// </summary>
    public static partial class ChapterTitleHelper
    {
        [GeneratedRegex(@"(?<!\d)\.(\d+)$", RegexOptions.Compiled)]
        private static partial Regex TrailingSplitPartRegex();

        [GeneratedRegex(@"(?i)(?:^|[\s/\\_\-\[\(])(?:chương|chuong|chapter|chap|ch\.?)\s*[:#\-]?\s*(\d+(?:\.\d+)?)", RegexOptions.Compiled)]
        private static partial Regex ChapterKeywordRegex();

        [GeneratedRegex(@"(?i)(?:^|[\s/\\_\-\[\(])(tập|tap|phần|phan|vol\.?|ep\.?)\s*[:#\-]?\s*(\d+(?:\.\d+)?)", RegexOptions.Compiled)]
        private static partial Regex SectionKeywordRegex();

        [GeneratedRegex(@"^\s*0*(\d+(?:\.\d+)?)\b", RegexOptions.Compiled)]
        private static partial Regex LeadingNumberRegex();

        [GeneratedRegex(@"\b0*(\d+(?:\.\d+)?)\s*$", RegexOptions.Compiled)]
        private static partial Regex TrailingNumberRegex();

        /// <summary>
        /// Formats a raw chapter title into a short chapter label showing only the chapter number
        /// (e.g., "Chương 10" or "Chương 10.1") when possible.
        /// </summary>
        public static string FormatShortChapterTitle(string? rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return string.Empty;
            }

            string trimmed = rawTitle.Trim();

            // Use the deepest path segment first (e.g., "TruyenA/Vol 1/Chap 3" -> "Chap 3").
            int lastSlash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            string leaf = lastSlash >= 0 && lastSlash < trimmed.Length - 1
                ? trimmed[(lastSlash + 1)..].Trim()
                : trimmed;

            // Preserve any oversized-chapter split suffix appended after non-digits (e.g., "... - NetTruyen.1").
            string splitSuffix = string.Empty;
            Match splitMatch = TrailingSplitPartRegex().Match(leaf);
            if (splitMatch.Success)
            {
                splitSuffix = $".{splitMatch.Groups[1].Value}";
                leaf = leaf[..splitMatch.Index].TrimEnd();
            }

            // 1. Primary chapter keyword match ("Chương 10", "Chap 10", "Chapter 10", "Ch.10").
            Match chapterMatch = ChapterKeywordRegex().Match(leaf);
            if (chapterMatch.Success)
            {
                string num = NormalizeNumberString(chapterMatch.Groups[1].Value);
                return $"Chương {num}{splitSuffix}";
            }

            // 2. Secondary section keyword match ("Tập 2", "Phần 3", "Vol 1", "Ep 5").
            Match sectionMatch = SectionKeywordRegex().Match(leaf);
            if (sectionMatch.Success)
            {
                string keyword = NormalizeSectionKeyword(sectionMatch.Groups[1].Value);
                string num = NormalizeNumberString(sectionMatch.Groups[2].Value);
                return $"{keyword} {num}{splitSuffix}";
            }

            // 3. Pure numeric or leading/trailing numbered folder name ("001", "10 - Opening").
            Match leadingNum = LeadingNumberRegex().Match(leaf);
            if (leadingNum.Success)
            {
                string num = NormalizeNumberString(leadingNum.Groups[1].Value);
                return $"Chương {num}{splitSuffix}";
            }

            Match trailingNum = TrailingNumberRegex().Match(leaf);
            if (trailingNum.Success)
            {
                string num = NormalizeNumberString(trailingNum.Groups[1].Value);
                return $"Chương {num}{splitSuffix}";
            }

            return string.IsNullOrEmpty(splitSuffix) ? leaf : $"{leaf}{splitSuffix}";
        }

        private static string NormalizeNumberString(string value)
        {
            if (string.IsNullOrEmpty(value)) return "0";
            int dotIndex = value.IndexOf('.');
            if (dotIndex < 0)
            {
                string stripped = value.TrimStart('0');
                return string.IsNullOrEmpty(stripped) ? "0" : stripped;
            }

            string intPart = value[..dotIndex].TrimStart('0');
            if (string.IsNullOrEmpty(intPart)) intPart = "0";
            return $"{intPart}{value[dotIndex..]}";
        }

        private static string NormalizeSectionKeyword(string keyword)
        {
            string lower = keyword.TrimEnd('.').ToLowerInvariant();
            return lower switch
            {
                "tap" or "tập" => "Tập",
                "phan" or "phần" => "Phần",
                "vol" => "Vol",
                "ep" => "Ep",
                _ => keyword.TrimEnd('.')
            };
        }
    }
}
