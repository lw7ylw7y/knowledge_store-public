namespace BookSearch.Tests;

/// <summary>The name and the icon (ST-29): the app is called Docent wherever the user sees a name.</summary>
public class BrandingTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "BookSearch.App"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    [Fact]
    public void Icon_File_IsAValidIconWithEverySizeWindowsAsksFor()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "src", "BookSearch.App", "Assets", "Docent.ico"));

        Assert.Equal((0, 1), (BitConverter.ToInt16(bytes, 0), BitConverter.ToInt16(bytes, 2)));        // reserved 0, type 1 = icon
        var count = BitConverter.ToInt16(bytes, 4);
        var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + i * 16] == 0 ? 256 : (int)bytes[6 + i * 16]).Order().ToList();
        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 96, 128, 256], sizes);
        for (var i = 0; i < count; i++)                                                                // every entry is a PNG inside the file
        {
            var length = BitConverter.ToInt32(bytes, 6 + i * 16 + 8);
            var offset = BitConverter.ToInt32(bytes, 6 + i * 16 + 12);
            Assert.True(offset + length <= bytes.Length);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[offset..(offset + 4)]);
        }
    }

    [Fact]
    public void Icon_IsUsedByTheProgramTheWindowAndTheInstaller()
    {
        Assert.Contains("<ApplicationIcon>Assets\\Docent.ico</ApplicationIcon>", Read("src", "BookSearch.App", "BookSearch.App.csproj"));
        Assert.Contains("Assets\\Docent.ico", Read("src", "BookSearch.App", "BookSearch.App.csproj").Replace("<ApplicationIcon>", ""));
        Assert.Contains("SetIcon(", Read("src", "BookSearch.App", "MainWindow.xaml.cs"));
        var iss = Read("scripts", "installer", "BookSearch.iss");
        Assert.Contains("SetupIconFile=..\\..\\src\\BookSearch.App\\Assets\\Docent.ico", iss);
        Assert.True(File.Exists(Path.Combine(Root(), "scripts", "installer", "..", "..", "src", "BookSearch.App", "Assets", "Docent.ico")));
    }

    [Fact]
    public void Name_EverythingTheUserSees_SaysDocent()
    {
        var resources = Read("src", "BookSearch.App", "Strings", "en-US", "Resources.resw");
        var iss = Read("scripts", "installer", "BookSearch.iss");

        Assert.Contains("<data name=\"App_Title\" xml:space=\"preserve\"><value>Docent</value>", resources);
        Assert.DoesNotContain("Local Book Search", resources);
        Assert.DoesNotContain("Local Book Search", iss);
        Assert.Contains("AppName=Docent", iss);
        Assert.Contains("OutputBaseFilename=Docent-Setup-", iss);
        Assert.Contains("Docent-Setup-$Version.exe", Read("scripts", "build-release.ps1"));
        Assert.StartsWith("<p align=\"center\"><img src=\"docs/logo/docent-256.png\"", Read("README.md"));
    }

    [Fact]
    public void Name_TheDataFolderKeepsItsOldNameSoExistingLibrariesAreFound()
    {
        // Renaming it would hide every existing library; it is moved only by a deliberate migration (not built).
        Assert.Contains("\"BookSearch\"", Read("src", "BookSearch.App.Logic", "AppPaths.cs"));
        Assert.Contains("{localappdata}\\BookSearch", Read("scripts", "installer", "BookSearch.iss"));
    }

    [Fact]
    public void Logo_BothDrawingsAreInTheRepository()
    {
        Assert.Contains("<svg", Read("docs", "logo", "docent.svg"));
        Assert.Contains("<svg", Read("docs", "logo", "docent-small.svg"));
        Assert.True(File.Exists(Path.Combine(Root(), "docs", "logo", "docent-256.png")));
    }
}
