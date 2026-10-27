using BookSearch.App.Reader;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BookSearch.App.Pages;

/// <summary>A chapter as text, scrolled to the passage a search result came from, with the question's words marked.</summary>
public sealed partial class ReaderPage : Page
{
    public ReaderPage() => InitializeComponent();

    public ReaderViewModel ViewModel { get; private set; } = null!;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var navigation = (ReaderNavigation)e.Parameter;
        ViewModel = navigation.Reader;
        ViewModel.ScrollRequested += ScrollTo;
        Bindings.Update();
        StartPositionTimer();
        try
        {
            if (navigation.BookId is { } bookId) await ViewModel.OpenBookAsync(bookId);
            else if (navigation.Request is { } request) await ViewModel.OpenAsync(request);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ScrollRequested -= ScrollTo;
        _positionTimer?.Stop();
        try { await ViewModel.SavePlaceAsync(); }            // leaving: the place is saved now, not on the next tick
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private DispatcherQueueTimer? _positionTimer;

    /// <summary>Once a second, tells the view model which paragraph is at the top of the list, so the place is remembered.</summary>
    private void StartPositionTimer()
    {
        _positionTimer ??= DispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromSeconds(1);
        _positionTimer.Tick -= PositionTimer_Tick;
        _positionTimer.Tick += PositionTimer_Tick;
        _positionTimer.Start();
    }

    private async void PositionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (ParagraphList.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0) return;
        try { await ViewModel.NoteVisibleParagraphAsync(panel.FirstVisibleIndex); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>Scrolls the chapter so the given paragraph is at the top (after the list has taken the new paragraphs).</summary>
    private void ScrollTo(int index) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (index >= 0 && index < ViewModel.Paragraphs.Count)
                ParagraphList.ScrollIntoView(ViewModel.Paragraphs[index], ScrollIntoViewAlignment.Leading);
        });

    private void ErrorBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.Error = null;

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Back_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Frame.CanGoBack) Frame.GoBack();
    }
}
