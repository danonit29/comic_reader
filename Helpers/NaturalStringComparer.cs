using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ComicReader.Helpers
{
    /// <summary>
    /// Compares strings using natural numeric ordering (e.g., page_2.jpg before page_10.jpg).
    /// </summary>
    public sealed partial class NaturalStringComparer : IComparer<string>
    {
        [GeneratedRegex(@"(\d+)", RegexOptions.Compiled)]
        private static partial Regex DigitChunkRegex();

        /// <summary>
        /// Compares two strings naturally by splitting numeric and text segments.
        /// </summary>
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            string[] xParts = DigitChunkRegex().Split(x);
            string[] yParts = DigitChunkRegex().Split(y);
            int minLength = Math.Min(xParts.Length, yParts.Length);

            for (int i = 0; i < minLength; i++)
            {
                int result = CompareSegment(xParts[i], yParts[i]);
                if (result != 0) return result;
            }

            return xParts.Length.CompareTo(yParts.Length);
        }

        private static int CompareSegment(string left, string right)
        {
            if (int.TryParse(left, out int leftNum) && int.TryParse(right, out int rightNum))
            {
                return leftNum.CompareTo(rightNum);
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
