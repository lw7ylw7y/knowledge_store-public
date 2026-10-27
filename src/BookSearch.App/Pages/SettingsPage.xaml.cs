using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BookSearch.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    public SettingsViewModel ViewModel { get; private set; } = null!;

    private async Task LoadAsync()
    {
        try { await ViewModel.LoadAsync(); Bindings.Update(); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private void MessageBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissMessage();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (SettingsViewModel)e.Parameter;
        Bindings.Update();
        _ = LoadAsync();
    }
}
