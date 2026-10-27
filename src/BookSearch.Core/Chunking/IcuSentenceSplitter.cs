using System.Runtime.InteropServices;

namespace BookSearch.Core.Chunking;

/// <summary>
/// Sentence splitting with ICU's break iterator, which handles every script. On Windows it calls the system
/// <c>icu.dll</c>; elsewhere it probes for a versioned <c>libicuuc</c>. English-like locales use ICU's
/// sentence-break suppressions (<c>@ss=standard</c>) so "Dr. Smith" isn't split.
/// </summary>
public sealed unsafe class IcuSentenceSplitter : ISentenceSplitter
{
    private const int UbrkSentence = 3;
    private const int UbrkDone = -1;

    private delegate* unmanaged<int, byte*, char*, int, int*, nint> _open;
    private delegate* unmanaged<nint, int> _next;
    private delegate* unmanaged<nint, void> _close;

    private IcuSentenceSplitter() { }

    public static bool IsAvailable => TryCreate() is not null;

    public static IcuSentenceSplitter? TryCreate()
    {
        foreach (var (name, suffix) in Candidates())
        {
            if (!NativeLibrary.TryLoad(name, out var lib)) continue;
            if (!NativeLibrary.TryGetExport(lib, "ubrk_open" + suffix, out var open)) continue;
            if (!NativeLibrary.TryGetExport(lib, "ubrk_next" + suffix, out var next)) continue;
            if (!NativeLibrary.TryGetExport(lib, "ubrk_close" + suffix, out var close)) continue;
            return new IcuSentenceSplitter
            {
                _open = (delegate* unmanaged<int, byte*, char*, int, int*, nint>)open,
                _next = (delegate* unmanaged<nint, int>)next,
                _close = (delegate* unmanaged<nint, void>)close,
            };
        }
        return null;
    }

    private static IEnumerable<(string Library, string Suffix)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("icu.dll", "");   // Windows 10 1903+ exports unversioned names
            yield break;
        }
        for (var v = 90; v >= 55; v--)
        {
            if (OperatingSystem.IsMacOS()) yield return ($"libicuuc.{v}.dylib", $"_{v}");
            else yield return ($"libicuuc.so.{v}", $"_{v}");
        }
    }

    public IReadOnlyList<(int Start, int Length)> Split(string text, string? language)
    {
        if (text.Length == 0) return [];
        var locale = System.Text.Encoding.ASCII.GetBytes(LocaleFor(language) + "\0");

        // The iterator keeps a pointer into the text, so it stays pinned from open to close.
        var ends = new List<(int, int)>();
        fixed (byte* loc = locale)
        fixed (char* chars = text)
        {
            var status = 0;
            var iterator = _open(UbrkSentence, loc, chars, text.Length, &status);
            if (status > 0 || iterator == 0)
                throw new InvalidOperationException($"ICU ubrk_open failed (status {status}).");
            try
            {
                var start = 0;
                int boundary;
                while ((boundary = _next(iterator)) != UbrkDone)
                {
                    ends.Add((start, boundary));
                    start = boundary;
                }
            }
            finally
            {
                _close(iterator);
            }
        }
        return SentenceSplitters.MergeAbbreviations(text, SentenceSplitters.Trim(text, ends));
    }

    private static string LocaleFor(string? language) =>
        string.IsNullOrEmpty(language) || language == "und" ? "en@ss=standard" : $"{language}@ss=standard";
}
