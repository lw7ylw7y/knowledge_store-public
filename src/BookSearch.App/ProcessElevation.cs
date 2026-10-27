using System.Security.Principal;

namespace BookSearch.App;

internal static class ProcessElevation
{
    /// <summary>True when the app was started "as administrator". Windows then blocks drag and drop from a normal Explorer.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
