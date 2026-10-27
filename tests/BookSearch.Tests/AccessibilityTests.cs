using System.Text.RegularExpressions;
using System.Xml.Linq;
using BookSearch.App;
using BookSearch.App.Ask;
using BookSearch.App.History;
using static BookSearch.Tests.SummaryKit;

namespace BookSearch.Tests;

/// <summary>Accessibility (ST-20): every control has a name a screen reader can say, no colours are fixed in XAML, and Esc stops a summary.</summary>
public class AccessibilityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string AutomationName = "AutomationProperties.Name";

    private static string AppFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BookSearch.App");
            if (Directory.Exists(Path.Combine(candidate, "Pages"))) return candidate;
        }
        throw new DirectoryNotFoundException("src/BookSearch.App was not found above the test folder.");
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppFolder(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static HashSet<string> ResourceKeys() =>
        XDocument.Load(Path.Combine(AppFolder(), "Strings", "en-US", "Resources.resw")).Root!
            .Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

    /// <summary>Controls a user can operate. ItemsControl is a plain container and has no name of its own.</summary>
    private static readonly HashSet<string> Interactive =
    [
        "Button", "ToggleButton", "HyperlinkButton", "TextBox", "PasswordBox", "AutoSuggestBox", "ToggleSwitch", "ComboBox",
        "NumberBox", "Slider", "CheckBox", "RadioButton", "RadioButtons", "Expander", "ListView", "NavigationViewItem", "AppBarButton",
    ];

    [Fact]
    public void Xaml_InteractiveControls_HaveAutomationName()
    {
        var keys = ResourceKeys();
        var unnamed = new List<string>();
        foreach (var file in XamlFiles())
        {
            foreach (var e in XDocument.Load(file).Descendants().Where(e => Interactive.Contains(e.Name.LocalName)))
            {
                var uid = (string?)e.Attribute(X + "Uid");
                var named = e.Attribute(AutomationName) is not null
                            || !string.IsNullOrWhiteSpace((string?)e.Attribute("Content"))
                            || !string.IsNullOrWhiteSpace((string?)e.Attribute("Header"))
                            || !string.IsNullOrWhiteSpace((string?)e.Attribute("Label"))
                            || (uid is not null && new[] { AutomationName, "Content", "Header", "Label" }.Any(p => keys.Contains($"{uid}.{p}")));
                if (!named) unnamed.Add($"{Path.GetFileName(file)}: <{e.Name.LocalName}{(uid is null ? "" : " x:Uid=" + uid)}>");
            }
        }
        Assert.Empty(unnamed);
    }

    [Fact]
    public void Xaml_UidsPointAtExistingResources()
    {
        var keys = ResourceKeys();
        var missing = XamlFiles()
            .SelectMany(f => XDocument.Load(f).Descendants().Select(e => (string?)e.Attribute(X + "Uid")).OfType<string>()
                .Where(uid => !keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal))).Select(uid => $"{Path.GetFileName(f)}: {uid}"))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Xaml_UsesThemeResourcesNotFixedColours()
    {
        // Fixed colours break dark mode and high contrast (NFR-22). Brushes come from ThemeResource.
        var fixedColour = new Regex("\"(#[0-9A-Fa-f]{3,8}|Red|Green|Blue|Black|White|Gray|Grey|Orange|Yellow|Transparent)\"");
        var found = XamlFiles()
            .SelectMany(f => XDocument.Load(f).Descendants().SelectMany(e => e.Attributes()
                .Where(a => a.Name.LocalName is "Foreground" or "Background" or "BorderBrush" or "Fill" or "Stroke" or "Color")
                .Where(a => fixedColour.IsMatch("\"" + a.Value + "\"") && a.Value != "Transparent")
                .Select(a => $"{Path.GetFileName(f)}: {a.Name.LocalName}={a.Value}")))
            .ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void Xaml_PageWideAccelerators_ShowNoTooltip()
    {
        // An accelerator declared on a Page makes the whole page its owner, and WinUI would show "Esc" or "Ctrl+F" over any text.
        var bad = XamlFiles()
            .SelectMany(f => XDocument.Load(f).Descendants()
                .Where(e => e.Name.LocalName.EndsWith(".KeyboardAccelerators", StringComparison.Ordinal))
                .Select(e => e.Parent!)
                .Where(owner => (string?)owner.Attribute("KeyboardAcceleratorPlacementMode") != "Hidden")
                .Select(owner => $"{Path.GetFileName(f)}: {owner.Name.LocalName}"))
            .ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void ResultAndThreadRows_ReadAsOneSentenceToAScreenReader()
    {
        var row = Rows(1)[0];
        Assert.Contains(row.Title, row.AutomationName);
        var thread = new ThreadRow(new BookSearch.Core.Data.SearchThread(1, DateTime.UtcNow, DateTime.UtcNow, "What is a rabbit hole?", 3), new Strings());
        Assert.StartsWith("What is a rabbit hole?, ", thread.AutomationName);
        Assert.Contains("History_ThreadMany", thread.AutomationName);
    }

    [Fact]
    public async Task Keyboard_EscWhileSummarizing_Stops()
    {
        var started = new TaskCompletionSource();
        var summarizer = new FakeSummarizer
        {
            OnRun = async (_, _, _, onToken, ct) => { onToken("Partial"); started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Run("x"); },
        };
        var history = new InMemoryHistory();
        using var coordinator = Coordinator(summarizer, history);
        var vm = new AskViewModel(new FakeAsk(), new Strings(), coordinator, new Settings(), history, a => a()) { Question = "a topic" };
        await vm.SearchCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.Turns[0].Summary.IsRunning);

        await vm.StopSummaryCommand.ExecuteAsync(null);                      // Esc
        await coordinator.WhenIdleAsync();

        Assert.False(vm.Turns[0].Summary.IsRunning);
        Assert.Equal("Ask_SummaryStopped", vm.Turns[0].Results[0].SummaryText);
    }

    [Fact]
    public async Task Keyboard_EscWithNothingRunning_DoesNothing()
    {
        var history = new InMemoryHistory();
        using var coordinator = Coordinator(new FakeSummarizer(), history);
        var vm = new AskViewModel(new FakeAsk(), new Strings(), coordinator, new Settings(), history, a => a());

        await vm.StopSummaryCommand.ExecuteAsync(null);                      // no turns at all

        Assert.Empty(vm.Turns);
    }
}
