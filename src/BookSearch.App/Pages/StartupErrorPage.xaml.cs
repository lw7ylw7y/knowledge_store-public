using Microsoft.UI.Xaml.Controls;

namespace BookSearch.App.Pages;

/// <summary>Shown instead of the shell when the library database can't be opened (a missing vec0.dll, for example).</summary>
public sealed partial class StartupErrorPage : Page
{
    public StartupErrorPage(string detail)
    {
        InitializeComponent();
        Detail.Text = detail;
    }
}
