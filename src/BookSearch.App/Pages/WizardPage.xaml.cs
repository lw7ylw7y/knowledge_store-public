using BookSearch.App.Wizard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinRT.Interop;

namespace BookSearch.App.Pages;

public sealed partial class WizardPage : Page
{
    private readonly Window _window;
    private bool _syncingTier;

    public WizardPage(WizardViewModel viewModel, Window window)
    {
        ViewModel = viewModel;
        _window = window;
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WizardViewModel.SelectedTier)) SelectCurrentTier();
        };
        Loaded += (_, _) => SelectCurrentTier();
    }

    public WizardViewModel ViewModel { get; }

    private void SelectCurrentTier()
    {
        _syncingTier = true;
        TierList.SelectedItem = ViewModel.TierOptions.FirstOrDefault(o => o.Tier == ViewModel.SelectedTier);
        _syncingTier = false;
    }

    private void TierList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTier && TierList.SelectedItem is TierOption option) ViewModel.SelectedTier = option.Tier;
    }

    private void ErrorBar_CloseButtonClick(InfoBar sender, object args) => ViewModel.DismissError();

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var strings = App.Services.GetRequiredService<IStrings>();
            var files = Win32FilePicker.Pick(WindowNative.GetWindowHandle(_window),
                strings.Get("Wizard_PickerTitle"), strings.Get("Wizard_PickerFilter"), "*.gguf");
            if (files.Count > 0) await ViewModel.ImportCommand.ExecuteAsync(files);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            ViewModel.ReportPickerFailure(CrashLog.Describe(ex));
        }
    }
}
