namespace BookSearch.App;

/// <summary>
/// Looks up UI text by resource key (NFR-17). The app backs it with the .resw files; view models use it for the
/// sentences they build at run time, such as progress lines. Static labels are set in XAML with <c>x:Uid</c>.
/// </summary>
public interface IStrings
{
    string Get(string key);
}

public static class StringsExtensions
{
    public static string Format(this IStrings strings, string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, strings.Get(key), args);
}
