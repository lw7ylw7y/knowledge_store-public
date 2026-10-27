using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BookSearch.Tests;

/// <summary>NFR-17: no UI text in XAML or C#; it all comes from Strings\en-US\Resources.resw.</summary>
public partial class AppResourcesTests
{
    private static readonly string AppFolder = FindApp();

    private static string FindApp()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BookSearch.sln")))
                return Path.Combine(dir.FullName, "src", "BookSearch.App");
        throw new DirectoryNotFoundException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    private const string CodeKeys =
        @"(?:strings|_strings)\.(?:Get|Format)\(\s*""([A-Za-z_]+)""|""((?:Wizard|Settings|Library|Ask|Reader|History)_[A-Za-z]+|App_Title)""";

    private static IEnumerable<string> XamlFiles() => Directory.EnumerateFiles(AppFolder, "*.xaml", SearchOption.AllDirectories);

    private static HashSet<string> ResourceKeys() =>
        XDocument.Load(Path.Combine(AppFolder, "Strings", "en-US", "Resources.resw"))
            .Descendants("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

    // Attributes that show text to the user. A value that starts with '{' is a binding or a resource reference.
    [GeneratedRegex(@"\b(Text|Content|Header|Title|PlaceholderText|Description|Label|Message|ToolTip|ToolTipService\.ToolTip|AutomationProperties\.(Name|HelpText))\s*=\s*""(?!\{)([^""]*)""")]
    private static partial Regex LiteralText();

    [Fact]
    public void Strings_NoHardcodedTextInXaml()
    {
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
            foreach (Match m in LiteralText().Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");

        Assert.True(offenders.Count == 0, "Literal UI text in XAML (use x:Uid and Resources.resw):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Strings_EveryXUid_HasAResourceEntry()
    {
        var keys = ResourceKeys();
        var missing = new List<string>();
        foreach (var file in XamlFiles())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "x:Uid=\"([^\"]+)\""))
                if (!keys.Any(k => k.StartsWith(m.Groups[1].Value + ".", StringComparison.Ordinal)))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");

        Assert.True(missing.Count == 0, "x:Uid without a Resources.resw entry:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Strings_EveryKeyUsedInCode_HasAResourceEntry()
    {
        var keys = ResourceKeys();
        var logic = Path.Combine(AppFolder, "..", "BookSearch.App.Logic");
        var files = Directory.EnumerateFiles(logic, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(AppFolder, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        var missing = new List<string>();
        foreach (var file in files)
            foreach (Match m in Regex.Matches(File.ReadAllText(file), CodeKeys))
                foreach (var g in m.Groups.Cast<Group>().Skip(1).Where(g => g.Success))
                    if (!keys.Contains(g.Value)) missing.Add($"{Path.GetFileName(file)}: {g.Value}");

        Assert.True(missing.Count == 0, "String keys used in code but missing from Resources.resw:\n" + string.Join("\n", missing.Distinct()));
    }
}
