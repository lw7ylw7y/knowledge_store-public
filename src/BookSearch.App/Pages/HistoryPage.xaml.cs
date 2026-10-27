using BookSearch.App.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BookSearch.App.Pages;

/// <summary>Every conversation, the one with the latest question first. Questions fill in live while summaries are being written.</summary>
public sealed partial class HistoryPage : Page
{
    public HistoryPage() => InitializeComponent();

    public HistoryViewModel ViewModel { get; private set; } = null!;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (HistoryViewModel)e.Parameter;
        Bindings.Update();
        try { await ViewModel.ReloadAsync(); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ThreadRow row) return;
        try { await ViewModel.CloseCommand.ExecuteAsync(row); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ThreadRow row) ViewModel.ContinueCommand.Execute(row);
    }
}
