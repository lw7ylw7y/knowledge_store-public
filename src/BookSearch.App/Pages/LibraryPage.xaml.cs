using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.Extensions.DependencyInjection;
using WinRT.Interop;

namespace BookSearch.App.Pages;

public sealed partial class LibraryPage : Page
{
    private DispatcherQueueTimer? _timer;

    public LibraryPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;   // Back from the Reader returns to this page as it was (scroll position)
        Loaded += (_, _) => StartPolling();
        Unloaded += (_, _) => _timer?.Stop();
    }

    public LibraryViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (LibraryViewModel)e.Parameter;
        ViewModel.CancelSelectionCommand.Execute(null);   // a visit starts with nothing ticked and no selection mode
        Bindings.Update();
        _ = RefreshPlacesAsync();                         // the Reader may have moved a book's place while this page was away
    }

    private async Task RefreshPlacesAsync()
    {
        try { await ViewModel.RefreshPlacesAsync(); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>Asks the worker's progress rows about once a second; only changed rows are touched.</summary>
    private void StartPolling()
    {
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) =>
        {
            try { await ViewModel.PollAsync(); }
            catch (Exception ex) { CrashLog.Write(ex); }
        };
        _timer.Start();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var strings = App.Services.GetRequiredService<IStrings>();
            var files = Win32FilePicker.Pick(WindowNative.GetWindowHandle(App.MainWindow),
                strings.Get("Library_PickerTitle"), strings.Get("Library_PickerFilter"),
                string.Join(";", LibraryViewModel.SupportedExtensions.Select(x => "*" + x)));
            if (files.Count > 0) await AddAsync(files);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ViewModel.ReportPickerFailure(CrashLog.Describe(ex));
        }
    }

    private void ElevationBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissElevationNotice();
    private void BannerBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissBanner();
    private void NoticeBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissNotice();

    private void Page_DragOver(object sender, DragEventArgs e) =>
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            await AddAsync(items.OfType<StorageFile>().Select(f => f.Path));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ViewModel.ReportDropFailure(CrashLog.Describe(ex));
        }
    }

    private async Task AddAsync(IEnumerable<string> paths)
    {
        try { await ViewModel.AddFilesAsync(paths); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>A click or Enter on a row does the row's main action (read it, or open its file).</summary>
    private void BookList_ItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenCommand.Execute(e.ClickedItem as BookRow);

    private void Open_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenCommand.Execute((sender as FrameworkElement)?.DataContext as BookRow);

    /// <summary>
    /// The row's "..." and context menus: their items are not in the row's visual tree, so they do not inherit its DataContext;
    /// when the menu opens, each item is told which book it is for.
    /// </summary>
    private void RowMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout || flyout.Target?.DataContext is not BookRow row) return;
        if (ViewModel.IsSelecting)                          // choosing books: a row's menu is not offered (a click ticks)
        {
            flyout.Hide();
            return;
        }
        foreach (var item in flyout.Items) item.Tag = row;
    }

    private async void Tags_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookRow row) return;
        try { await ViewModel.EditTagsCommand.ExecuteAsync(row); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookRow row) return;
        try { await ViewModel.RemoveCommand.ExecuteAsync(row); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }
}
