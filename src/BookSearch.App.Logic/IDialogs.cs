namespace BookSearch.App;

/// <summary>Questions the view models ask the user. The app implements this with ContentDialogs.</summary>
public interface IDialogs
{
    Task<bool> ConfirmRemoveAsync(string bookTitle);

    /// <summary>Asks before the whole search history is deleted.</summary>
    Task<bool> ConfirmClearHistoryAsync();

    /// <summary>Asks before a thread of more than one question is deleted from the History.</summary>
    Task<bool> ConfirmDeleteThreadAsync(int questions);

    /// <summary>Shows the tags and books to filter by with <paramref name="current"/> ticked; returns the new filter, or null if cancelled.</summary>
    Task<BookSearch.Core.Search.SearchFilter?> ChooseFilterAsync(Ask.FilterOptions options, BookSearch.Core.Search.SearchFilter current);

    /// <summary>Asks for the tags of one book (a comma-separated list), with the tags already in use offered; null if cancelled.</summary>
    Task<IReadOnlyList<string>?> EditTagsAsync(string bookTitle, IReadOnlyList<string> current, IReadOnlyList<string> allTags);

    /// <summary>Asks which tags to add to <paramref name="books"/> selected books; null if cancelled.</summary>
    Task<IReadOnlyList<string>?> AddTagsAsync(int books, IReadOnlyList<string> allTags);

    /// <summary>Asks before "Delete all data": cancel, delete the library but keep the models, or delete the models too.</summary>
    Task<DeleteAllChoice> ConfirmDeleteAllAsync();
}

public enum DeleteAllChoice { Cancel, KeepModels, DeleteModels }
