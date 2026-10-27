using System.Text.Json;
using System.Text.RegularExpressions;

namespace BookSearch.Tests;

/// <summary>
/// The installer's scripts (ST-21). They run only on Windows, so these tests check what can be checked here: that the Inno Setup
/// script keeps its promises and the build script has its guards. The real checks (checksum file against the installer, size)
/// run in the "Release installer" workflow. The llama.cpp pin is in the manifest and is tested with the other models.
/// </summary>
public class ReleaseTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "build-release.ps1"))) return dir.FullName;
        throw new DirectoryNotFoundException("scripts/build-release.ps1 was not found above the test folder.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    [Fact]
    public void Release_BuildScript_WritesTheChecksumFileAndEnforcesTheSizeLimit()
    {
        var script = Read("scripts", "build-release.ps1");

        Assert.Contains("$limit = 200MB", script);
        Assert.Contains("Get-FileHash -Algorithm SHA256", script);
        Assert.Contains("\"$installer.sha256\"", script);
        Assert.Matches(@"\$hash  \$\(Split-Path -Leaf \$installer\)", script);                      // "<hash>  <file name>"
        Assert.Contains("if ($size -gt $limit) { throw", script);
    }

    [Fact]
    public void Release_Installer_DoesNotBundleLlamaServer_TheAppDownloadsIt()
    {
        var build = Read("scripts", "build-release.ps1");
        var iss = Read("scripts", "installer", "BookSearch.iss");

        Assert.DoesNotContain("llama-server", build);                    // nothing fetches or copies it into the publish folder
        Assert.DoesNotContain("llama-server", iss);
        Assert.Contains("\"role\": \"runtime\"", Read("scripts", "pin-llama-server.ps1"));             // the pin script prints a manifest entry
    }

    [Fact]
    public void Release_Workflow_ComparesTheChecksumFileWithTheInstaller()
    {
        var workflow = Read(".github", "workflows", "release.yml");

        Assert.Contains("scripts\\build-release.ps1", workflow);
        Assert.Contains("Get-FileHash -Algorithm SHA256", workflow);
        Assert.Contains("Checksum file says", workflow);
        Assert.Contains("choco install innosetup", workflow);
        Assert.DoesNotContain("release-please", workflow);
    }

    [Fact]
    public void Installer_Script_InstallsPerUserWithoutAdminAndKeepsDataByDefault()
    {
        var iss = Read("scripts", "installer", "BookSearch.iss");

        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", iss);                             // no way to ask for admin
        Assert.Contains("DefaultDirName={autopf}\\Docent", iss);                                  // per-user: %LOCALAPPDATA%\Programs
        Assert.Contains("MinVersion=10.0.19045", iss);                                                // Windows 10 22H2
        Assert.Contains("ArchitecturesAllowed=x64compatible", iss);
        Assert.Contains("{autoprograms}\\Docent", iss);                                    // Start menu shortcut
        Assert.Contains("MB_DEFBUTTON2", iss);                                                        // the default answer is No: keep the data
        Assert.Contains("UninstallSilent() or not DirExists(DataDir)", iss);                          // silent uninstall keeps it too
        Assert.Matches(@"DataDir := ExpandConstant\('\{localappdata\}\\BookSearch'\)", iss);          // the folder the app uses
    }

    [Fact]
    public void Installer_DataFolder_MatchesTheFolderTheAppUses()
    {
        var paths = Read("src", "BookSearch.App.Logic", "AppPaths.cs");
        var iss = Read("scripts", "installer", "BookSearch.iss");

        Assert.Matches(new Regex(@"SpecialFolder\.LocalApplicationData\), ""BookSearch"""), paths);
        Assert.Contains("{localappdata}\\BookSearch", iss);
    }
}
