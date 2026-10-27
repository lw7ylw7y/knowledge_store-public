namespace BookSearch.App.Ask;

/// <summary>Decides whether a passage reads right to left (NFR-18) from the letters it contains.</summary>
public static class TextDirection
{
    public static bool IsRightToLeft(string text)
    {
        int rtl = 0, ltr = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            // Hebrew, Arabic, Syriac, Thaana, NKo and the Arabic presentation forms.
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') rtl++;
            else ltr++;
        }
        return rtl > ltr;
    }
}
