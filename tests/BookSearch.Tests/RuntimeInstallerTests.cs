using System.IO.Compression;
using System.Security.Cryptography;
using BookSearch.App;
using BookSearch.Models.Management;

namespace BookSearch.Tests;

/// <summary>Installing the llama.cpp program from its pinned zip (ST-21): download or import, then unpack into bin.</summary>
public class RuntimeInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "runtime-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(_dir);
    private ModelStore Store => new(Paths.ModelsFolder);
    private string App => Path.Combine(_dir, "app");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>A zip like llama.cpp's: llama-server.exe and a library at the top level.</summary>
    private ModelEntry MakeZip(string name = "llama-b1-bin-win-cpu-x64.zip", bool withServer = true, string serverBody = "exe")
    {
        Directory.CreateDirectory(Paths.ModelsFolder);
        var path = Path.Combine(Paths.ModelsFolder, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (withServer) using (var w = new StreamWriter(zip.CreateEntry("llama-server.exe").Open())) w.Write(serverBody);
            using (var w = new StreamWriter(zip.CreateEntry("ggml.dll").Open())) w.Write("lib");
        }
        return new ModelEntry("llama-server-b1", ModelRole.Runtime, null, name, "https://example.test/" + name, Sha(path),
            new FileInfo(path).Length, "MIT", "test", []);
    }

    [Fact]
    public void Runtime_Install_UnpacksIntoBinWritesTheMarkerAndRemovesTheZip()
    {
        var entry = MakeZip();
        var installer = new RuntimeInstaller(Paths, Store, App);
        Assert.False(installer.IsInstalled(entry));

        installer.Install(entry);

        var bin = Path.Combine(_dir, "bin");
        Assert.True(File.Exists(Path.Combine(bin, "llama-server.exe")));
        Assert.True(File.Exists(Path.Combine(bin, "ggml.dll")));
        Assert.Equal(entry.Sha256, File.ReadAllText(Path.Combine(bin, "llama-server.sha256")));
        Assert.False(File.Exists(Store.PathFor(entry)));                 // 19 MB not kept
        Assert.False(Directory.Exists(bin + ".new"));
        Assert.True(installer.IsInstalled(entry));
        Assert.Equal(Path.Combine(bin, "llama-server.exe"), ServerLocator.Find(Paths, App));   // the app finds it where the README says
    }

    [Fact]
    public void Runtime_NewerPin_ReplacesAnOlderDownload()
    {
        var old = MakeZip("old.zip", serverBody: "old");
        var installer = new RuntimeInstaller(Paths, Store, App);
        installer.Install(old);
        var newer = MakeZip("new.zip", serverBody: "new");

        Assert.False(installer.IsInstalled(newer));                      // the marker names the old build: download again

        installer.Install(newer);

        Assert.True(installer.IsInstalled(newer));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, "bin", "llama-server.exe")));
    }

    [Fact]
    public void Runtime_HandInstalledServer_CountsAsInstalledAndIsNotReplaced()
    {
        var entry = MakeZip();
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "llama-server.exe"), "mine");      // no marker: not ours

        Assert.True(new RuntimeInstaller(Paths, Store, App).IsInstalled(entry));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir, "bin", "llama-server.exe")));
    }

    [Fact]
    public void Runtime_ServerNextToTheApp_CountsAsInstalled()
    {
        var entry = MakeZip();
        Directory.CreateDirectory(Path.Combine(App, "bin"));
        File.WriteAllText(Path.Combine(App, "bin", "llama-server.exe"), "dev build");

        Assert.True(new RuntimeInstaller(Paths, Store, App).IsInstalled(entry));
    }

    [Fact]
    public void Runtime_ZipWithoutTheServer_FailsAndLeavesBinAlone()
    {
        var good = MakeZip("good.zip");
        var installer = new RuntimeInstaller(Paths, Store, App);
        installer.Install(good);
        var bad = MakeZip("bad.zip", withServer: false);

        var ex = Assert.Throws<IOException>(() => installer.Install(bad));

        Assert.Contains("llama-server.exe", ex.Message);
        Assert.True(File.Exists(Path.Combine(_dir, "bin", "llama-server.exe")));    // the working install survives
        Assert.False(Directory.Exists(Path.Combine(_dir, "bin.new")));
    }

    [Fact]
    public async Task Provisioner_RuntimeDownload_DownloadsThenUnpacks()
    {
        var entry = MakeZip();
        var bytes = File.ReadAllBytes(Store.PathFor(entry));
        File.Delete(Store.PathFor(entry));
        var http = new HttpClient(new FakeHandler(bytes));
        var manifest = new ModelManifest([entry]);
        var provisioner = new ModelProvisioner(manifest, Store, new ModelDownloader(http), new RuntimeInstaller(Paths, Store, App));
        Assert.False(provisioner.IsInstalled(entry));

        await provisioner.DownloadAsync(entry, new Progress<DownloadProgress>(), default);

        Assert.True(provisioner.IsInstalled(entry));
        Assert.True(File.Exists(Path.Combine(_dir, "bin", "llama-server.exe")));
    }

    [Fact]
    public async Task Provisioner_RuntimeImport_UnpacksAZipFromDisk()
    {
        var entry = MakeZip();
        var source = Path.Combine(_dir, "usb", entry.File);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Move(Store.PathFor(entry), source);
        var provisioner = new ModelProvisioner(new ModelManifest([entry]), Store, new ModelDownloader(new HttpClient()),
            new RuntimeInstaller(Paths, Store, App));

        var imported = await provisioner.ImportAsync(source, new Progress<DownloadProgress>(), default);

        Assert.Equal(ModelRole.Runtime, imported.Role);
        Assert.True(provisioner.IsInstalled(entry));
    }

    [Fact]
    public async Task Provisioner_WrongZip_IsRejectedAndNothingIsUnpacked()
    {
        var entry = MakeZip();
        var other = Path.Combine(_dir, "usb", "other.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, [1, 2, 3, 4]);                          // not the pinned file
        var provisioner = new ModelProvisioner(new ModelManifest([entry]), Store, new ModelDownloader(new HttpClient()),
            new RuntimeInstaller(Paths, Store, App));

        await Assert.ThrowsAsync<ModelRejectedException>(() => provisioner.ImportAsync(other, new Progress<DownloadProgress>(), default));

        Assert.False(Directory.Exists(Path.Combine(_dir, "bin")));
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
