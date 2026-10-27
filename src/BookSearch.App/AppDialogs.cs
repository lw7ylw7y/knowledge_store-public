using BookSearch.App.Ask;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BookSearch.App;

/// <summary>Dialogs for the view models. <see cref="XamlRoot"/> is set by the shell once it is on screen.</summary>
public sealed class AppDialogs(IStrings strings) : IDialogs
{
    public XamlRoot? XamlRoot { get; set; }

    public async Task<bool> ConfirmRemoveAsync(string bookTitle)
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = strings.Format("Library_RemoveTitle", bookTitle),
            Content = strings.Get("Library_RemoveBody"),
            PrimaryButtonText = strings.Get("Library_RemoveConfirm"),
            CloseButtonText = strings.Get("Library_RemoveCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmClearHistoryAsync()
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = strings.Get("History_ClearTitle"),
            Content = strings.Get("History_ClearBody"),
            PrimaryButtonText = strings.Get("History_ClearConfirm"),
            CloseButtonText = strings.Get("Library_RemoveCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<DeleteAllChoice> ConfirmDeleteAllAsync()
    {
        if (XamlRoot is null) return DeleteAllChoice.Cancel;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = strings.Get("Settings_DeleteAllTitle"),
            Content = new TextBlock { Text = strings.Get("Settings_DeleteAllBody"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = strings.Get("Settings_DeleteKeepModels"),
            SecondaryButtonText = strings.Get("Settings_DeleteWithModels"),
            CloseButtonText = strings.Get("Library_RemoveCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => DeleteAllChoice.KeepModels,
            ContentDialogResult.Secondary => DeleteAllChoice.DeleteModels,
            _ => DeleteAllChoice.Cancel,
        };
    }

    /// <summary>Tags and books as tick boxes (the books can be searched by name); applying with nothing ticked clears the filter.</summary>
    public async Task<SearchFilter?> ChooseFilterAsync(FilterOptions options, SearchFilter current)
    {
        if (XamlRoot is null) return null;
        var tagBoxes = options.Tags.Select(t => new CheckBox
        {
            Content = strings.Format("Filter_TagItem", t.Name, t.Books),
            Tag = t.Name,
            IsChecked = current.Tags.Any(c => Tags.KeyOf(c) == t.Key),
        }).ToList();
        var bookBoxes = options.Books.Select(b => new CheckBox
        {
            Content = b.Title,
            Tag = b,
            IsChecked = current.Books.Any(c => c.Id == b.Id),
        }).ToList();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = strings.Get("Filter_TagsHeading"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (tagBoxes.Count == 0) panel.Children.Add(new TextBlock { Text = strings.Get("Filter_NoTags"), TextWrapping = TextWrapping.Wrap });
        foreach (var box in tagBoxes) panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = strings.Get("Filter_BooksHeading"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
        var find = new TextBox { PlaceholderText = strings.Get("Filter_FindBook") };
        AutomationProperties.SetName(find, strings.Get("Filter_FindBook"));
        find.TextChanged += (_, _) =>
        {
            foreach (var box in bookBoxes)
                box.Visibility = find.Text.Length == 0 || ((FilterBook)box.Tag).Title.Contains(find.Text, StringComparison.CurrentCultureIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        panel.Children.Add(find);
        foreach (var box in bookBoxes) panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = strings.Get("Filter_DialogTitle"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = strings.Get("Filter_Apply"),
            SecondaryButtonText = strings.Get("Filter_Clear"),
            CloseButtonText = strings.Get("Library_RemoveCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => new SearchFilter(
                tagBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList(),
                bookBoxes.Where(b => b.IsChecked == true).Select(b => (FilterBook)b.Tag).ToList()),
            ContentDialogResult.Secondary => SearchFilter.None,
            _ => null,
        };
    }

    public Task<IReadOnlyList<string>?> EditTagsAsync(string bookTitle, IReadOnlyList<string> current, IReadOnlyList<string> allTags) =>
        AskTagsAsync(strings.Format("Tags_EditTitle", bookTitle), string.Join(", ", current), allTags);

    public Task<IReadOnlyList<string>?> AddTagsAsync(int books, IReadOnlyList<string> allTags) =>
        AskTagsAsync(strings.Format("Tags_AddTitle", books), "", allTags);

    private async Task<IReadOnlyList<string>?> AskTagsAsync(string title, string text, IReadOnlyList<string> allTags)
    {
        if (XamlRoot is null) return null;
        var box = new TextBox { Text = text, PlaceholderText = strings.Get("Tags_Hint"), AcceptsReturn = false };
        AutomationProperties.SetName(box, strings.Get("Tags_Hint"));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = strings.Get("Tags_Explain"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        if (allTags.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = strings.Format("Tags_InUse", string.Join(", ", allTags.Take(40))),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = strings.Get("Tags_Save"),
            CloseButtonText = strings.Get("Library_RemoveCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? Tags.Parse(box.Text) : null;
    }

    public async Task<bool> ConfirmDeleteThreadAsync(int questions)
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = strings.Get("History_DeleteThreadTitle"),
            Content = strings.Format("History_DeleteThreadBody", questions),
            PrimaryButtonText = strings.Get("History_DeleteThreadConfirm"),
            CloseButtonText = strings.Get("Library_RemoveCancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
