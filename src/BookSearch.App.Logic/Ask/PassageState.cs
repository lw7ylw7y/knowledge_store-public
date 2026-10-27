using CommunityToolkit.Mvvm.ComponentModel;

namespace BookSearch.App.Ask;

public enum PassageStatus
{
    /// <summary>Not read by the model yet.</summary>
    Waiting,
    /// <summary>Being written.</summary>
    Writing,
    /// <summary>The summary is complete.</summary>
    Written,
    /// <summary>The model found nothing in the passage that helps with the question.</summary>
    NotCovered,
    /// <summary>The summary was stopped or failed before this result was done.</summary>
    Unfinished,
    /// <summary>The model answered, but wrote nothing for this result.</summary>
    Empty,
}

/// <summary>
/// The summary of one search result as it is written (ST-34): its state and the text so far. The properties change on the UI
/// thread only, so the result row can be bound to it.
/// </summary>
public sealed partial class PassageState(int number) : ObservableObject
{
    public int Number { get; } = number;

    [ObservableProperty] private PassageStatus _status = PassageStatus.Waiting;
    [ObservableProperty] private string _text = "";
}
