using Microsoft.Windows.ApplicationModel.Resources;

namespace BookSearch.App;

/// <summary>UI text from Strings\en-US\Resources.resw (NFR-17). A missing key shows the key itself, so it is easy to spot.</summary>
public sealed class ResourceStrings : IStrings
{
    private readonly ResourceLoader _loader = new();

    public string Get(string key)
    {
        try
        {
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }
}
