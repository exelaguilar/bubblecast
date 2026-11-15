using System;
using System.Globalization;
using System.Text;

namespace Bubblecast
{
    public static class DisplayTextSanitizer
    {
        public static string SanitizeAndTruncate(string value, int maximumTextElements)
        {
            if (String.IsNullOrEmpty(value) || maximumTextElements <= 0) return String.Empty;
            StringBuilder cleaned = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length;)
            {
                int codePoint = Char.ConvertToUtf32(value, index);
                int width = codePoint > 0xFFFF ? 2 : 1;
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(value, index);
                bool keepFormat = codePoint == 0x200D || IsVariationSelector(codePoint);
                if (!Char.IsControl(value, index) && (category != UnicodeCategory.Format || keepFormat))
                    cleaned.Append(value, index, width);
                index += width;
            }

            StringBuilder result = new StringBuilder(cleaned.Length);
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(cleaned.ToString());
            int count = 0;
            while (count < maximumTextElements && elements.MoveNext())
            {
                result.Append(elements.GetTextElement());
                count++;
            }
            return result.ToString().Trim();
        }

        private static bool IsVariationSelector(int codePoint)
        {
            return (codePoint >= 0xFE00 && codePoint <= 0xFE0F) ||
                   (codePoint >= 0xE0100 && codePoint <= 0xE01EF);
        }
    }
}
