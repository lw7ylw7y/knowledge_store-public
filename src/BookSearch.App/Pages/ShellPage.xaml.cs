using BookSearch.App.Ask;
using BookSearch.App.History;
using BookSearch.App.Reader;
using Microsoft.UI.Xaml.Controls;

namespace BookSearch.App.Pages;

/// <summary>The main window once models are installed: navigation on the left, one page on the right.</summary>
public sealed partial class ShellPage : Page
{
    private readonly LibraryViewModel _library;
    private readonly AskViewModel _ask;
    private readonly SettingsViewModel _settings;
    private readonly ReaderViewModel _reader;
    private readonly HistoryViewModel _history;

    public ShellPage(LibraryViewModel library, AskViewModel ask, SettingsViewModel settings, ReaderViewModel reader,
        HistoryViewModel history, AppDialogs dialogs)
    {
        _library = library;
        _reader = reader;
        _history = history;
        _ask = ask;
        _settings = settings;
        InitializeComponent();
        Loaded += (_, _) => dialogs.XamlRoot = XamlRoot;
        _ask.ResultOpened += request => ContentFrame.Navigate(typeof(ReaderPage), ReaderNavigation.ForPassage(_reader, request));   // Back returns to Ask
        // "Go to Library" in the Ask page's empty state.
        _ask.LibraryRequested += () => Nav.SelectedItem = LibraryItem;
        // A book chosen on the Library page opens in the Reader at its saved place; Back returns to the Library.
        _library.ReadRequested += bookId =>
        {
            if (ContentFrame.Content is not ReaderPage) ContentFrame.Navigate(typeof(ReaderPage), ReaderNavigation.ForBook(_reader, bookId));
        };
        // "Continue this thread" on a history thread opens the Ask page with that conversation.
        _history.ContinueRequested += threadId =>
        {
            Nav.SelectedItem = AskItem;
            _ = ContinueAsync(threadId);
        };
        _ = RestoreAsync();
        Nav.SelectedItem = AskItem;          // the app opens on Ask
    }

    private void Find_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Nav.SelectedItem = AskItem;   // selecting the item navigates (nothing happens if Ask is already shown)
        if (ContentFrame.Content is AskPage ask) DispatcherQueue.TryEnqueue(ask.FocusQuestion);
    }

    /// <summary>The Ask entry of the navigation bar, found by its tag so the order of the entries can change freely.</summary>
    private object LibraryItem => Nav.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "library");

    /// <summary>The Ask entry of the navigation bar, found by its tag so the order of the entries can change freely.</summary>
    private object AskItem => Nav.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag as string == "ask");

    private async Task ContinueAsync(long threadId)
    {
        try { await _ask.ShowThreadAsync(threadId); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>Brings back the conversation that was on screen when the app last closed.</summary>
    private async Task RestoreAsync()
    {
        try { await _ask.RestoreAsync(); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        switch ((args.SelectedItem as NavigationViewItem)?.Tag as string)
        {
            case "library": ContentFrame.Navigate(typeof(LibraryPage), _library); break;
            case "ask": ContentFrame.Navigate(typeof(AskPage), _ask); break;
            case "history": ContentFrame.Navigate(typeof(HistoryPage), _history); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage), _settings); break;
        }
    }
}
