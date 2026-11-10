using System.Globalization;
using System.Text;

namespace Remy.Core.Transforms;

/// <summary>Selection size limits (v0.2-features.md §8.4).</summary>
public static class TransformLimits
{
    public const int MaximumWords = 3000;
    public const int MaximumCharacters = 30000;

    /// <summary>Null when the selection may be sent; otherwise noSelection or selectionTooLong.</summary>
    public static TransformNotice? Validate(string text)
    {
        int words = WordCount(text);
        if (words == 0) return TransformNotice.NoSelection;
        if (words > MaximumWords || new StringInfo(text).LengthInTextElements > MaximumCharacters)
            return TransformNotice.SelectionTooLong(words);
        return null;
    }

    /// <summary>WINDOWS DECISION: maximal runs of letters/digits (an apostrophe or underscore between letters does not split),
    /// and each Han/kana/Hangul character counts as one word.</summary>
    public static int WordCount(string text)
    {
        int words = 0;
        bool inWord = false;
        var runes = text.EnumerateRunes().ToArray();
        for (int i = 0; i < runes.Length; i++)
        {
            var r = runes[i];
            if (IsCjk(r))
            {
                words++;
                inWord = false;
            }
            else if (Rune.IsLetterOrDigit(r))
            {
                if (!inWord) { words++; inWord = true; }
            }
            else if (inWord && IsJoiner(r) && i + 1 < runes.Length && Rune.IsLetterOrDigit(runes[i + 1]) && !IsCjk(runes[i + 1]))
            {
                // a joiner between letters stays inside the word
            }
            else inWord = false;
        }
        return words;
    }

    static bool IsJoiner(Rune r) => r.Value is '\'' or 0x2019 or '_';

    static bool IsCjk(Rune r)
    {
        int v = r.Value;
        return v is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F
            or >= 0x3040 and <= 0x309F or >= 0x30A0 and <= 0x30FF or >= 0x31F0 and <= 0x31FF
            or >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;
    }

    internal static string Group(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
