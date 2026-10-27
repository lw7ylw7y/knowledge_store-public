using System.Collections.Specialized;
using BookSearch.App.Ask;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace BookSearch.App.Pages;

public sealed partial class AskPage : Page
{
    public AskPage() => InitializeComponent();

    public AskViewModel ViewModel { get; private set; } = null!;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (AskViewModel)e.Parameter;
        ViewModel.Turns.CollectionChanged += OnTurnsChanged;
        ViewModel.FocusQuestionRequested += FocusAndSelectQuestion;
        ViewModel.FocusQuestionBoxRequested += FocusQuestion;
        Bindings.Update();
        ScrollToNewest();
        try { await ViewModel.RefreshAsync(); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Turns.CollectionChanged -= OnTurnsChanged;
        ViewModel.FocusQuestionRequested -= FocusAndSelectQuestion;
        ViewModel.FocusQuestionBoxRequested -= FocusQuestion;
    }

    /// <summary>The pencil button: the question is in the box; put the cursor there with the text selected, ready to change.</summary>
    private void FocusAndSelectQuestion()
    {
        QuestionBox.Focus(FocusState.Keyboard);
        QuestionBox.SelectAll();
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScrollToNewest();
    }

    /// <summary>The page shows the newest answer: after the new question has been laid out, scroll to the bottom.</summary>
    private void ScrollToNewest() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            TurnsScroll.UpdateLayout();
            TurnsScroll.ChangeView(null, TurnsScroll.ScrollableHeight, null);
        });

    /// <summary>Ctrl+F, and after a retry (the button that was pressed went away with its turn): put the cursor in the question box.</summary>
    public void FocusQuestion() => QuestionBox.Focus(FocusState.Keyboard);

    private void NotCoveredBar_CloseButtonClick(InfoBar sender, object args) => (sender.DataContext as TurnViewModel)?.DismissNotCovered();
    private void WarningBar_CloseButtonClick(InfoBar sender, object args) => (sender.DataContext as TurnViewModel)?.DismissWarning();
    private void IncompleteBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissIncompleteNotice();

    private void QuestionBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.SearchCommand.Execute(null);
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.StopSummaryCommand.Execute(null);
    }

    private void Result_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenResultCommand.Execute((sender as FrameworkElement)?.DataContext as ResultRow);
}
