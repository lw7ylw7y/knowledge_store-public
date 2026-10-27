using BookSearch.App.Ask;
using Microsoft.UI.Xaml;

namespace BookSearch.App;

/// <summary>
/// <c>FlowDirection="{x:Bind local:Rtl.For(Question), Mode=OneWay}"</c>: text in Arabic, Hebrew and similar scripts is drawn
/// right to left whatever the language of the rest of the page (NFR-18). The decision is <see cref="TextDirection"/>'s.
/// </summary>
public static class Rtl
{
    public static FlowDirection For(string? text) =>
        !string.IsNullOrEmpty(text) && TextDirection.IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
}
