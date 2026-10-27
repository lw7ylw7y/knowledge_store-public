using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using BookSearch.Core.Data;
using BookSearch.Models;
using BookSearch.Models.Management;
using BookSearch.Parsing;

namespace BookSearch.Tests;

/// <summary>Serves a byte array like a file host: with or without range requests, and optionally dropping the connection part-way.</summary>
internal sealed class FakeFileHost(byte[] content, bool supportsRange = true, long? dropAfterBytes = null) : HttpMessageHandler
{
    public List<string?> RangeHeaders { get; } = [];
    public long BytesServed { get; private set; }
    public int Requests { get; private set; }
    public long? DropAfterBytes { get; set; } = dropAfterBytes;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        RangeHeaders.Add(request.Headers.Range?.ToString());

        if (from is > 0 && supportsRange)
        {
            if (from >= content.Length) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = Body(content[(int)from.Value..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from.Value, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Body(content) });
    }

    private HttpContent Body(byte[] bytes) => new StreamContent(new CountingStream(bytes, DropAfterBytes, n => BytesServed += n));

    private sealed class CountingStream(byte[] bytes, long? dropAfter, Action<long> served) : Stream
    {
        private long _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (dropAfter is { } limit && _position >= limit) throw new IOException("connection reset (simulated)");
            var allowed = dropAfter is { } l ? (int)Math.Min(count, l - _position) : count;
            var n = (int)Math.Min(allowed, bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, n);
            _position += n;
            served(n);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class ModelManagementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "models-test-" + Guid.NewGuid().ToString("N"));
    private const long Gb = 1024L * 1024 * 1024;

    public ModelManagementTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] RandomBytes(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ModelEntry Entry(byte[] data, string file = "tiny.gguf", string? hash = null, ModelRole role = ModelRole.Llm,
        ModelTier? tier = ModelTier.Minimum) =>
        new("tiny", role, tier, file, "https://example.test/" + file, hash ?? Sha(data), data.Length, "Apache-2.0", "test", []);

    private ModelStore Store() => new(Path.Combine(_dir, "models"));

    // ---- tiers --------------------------------------------------------------------------------------------

    [Fact]
    public void Tier_8GB_Picks2B()
    {
        // An "8 GB" Windows PC reports about 7.8 GB.
        var tier = HardwareTiers.Select((long)(7.8 * Gb));

        Assert.Equal(ModelTier.Minimum, tier);
        Assert.Equal("qwen3.5-2b-q4_k_m", ModelManifest.LoadDefault().LlmFor(tier).Id);
    }

    [Fact]
    public void Tier_16GB_Picks4B()
    {
        var tier = HardwareTiers.Select((long)(15.9 * Gb));

        Assert.Equal(ModelTier.Recommended, tier);
        Assert.Equal("qwen3.5-4b-q4_k_m", ModelManifest.LoadDefault().LlmFor(tier).Id);
    }

    [Fact]
    public void Tier_BoundariesAndSmallMachines()
    {
        Assert.Equal(ModelTier.Recommended, HardwareTiers.Select(12 * Gb));
        Assert.Equal(ModelTier.Minimum, HardwareTiers.Select(12 * Gb - 1));
        Assert.Equal(ModelTier.Recommended, HardwareTiers.Select(64 * Gb));    // never the enhanced tier: no GPU support in v1
        Assert.Equal(ModelTier.Minimum, HardwareTiers.Select(4 * Gb));         // still the smallest model
        Assert.True(HardwareTiers.IsBelowMinimum(4 * Gb));
        Assert.False(HardwareTiers.IsBelowMinimum(8 * Gb));
        Assert.True(HardwareTiers.DetectTotalRamBytes() > 0);
    }

    // ---- manifest -----------------------------------------------------------------------------------------

    [Fact]
    public void Manifest_AllEntries_HaveHashAndFreeLicense()
    {
        var manifest = ModelManifest.LoadDefault();
        string[] freeLicenses = ["Apache-2.0", "MIT"];

        Assert.NotEmpty(manifest.Entries);
        foreach (var e in manifest.Entries)
        {
            Assert.Matches("^[0-9a-f]{64}$", e.Sha256);
            Assert.Contains(e.License, freeLicenses);
            Assert.StartsWith("https://", e.Url);
            Assert.False(string.IsNullOrWhiteSpace(e.Source));
            Assert.NotNull(e.ServerArgs);
        }
        foreach (var e in manifest.Entries.Where(e => e.Role != ModelRole.Runtime))
        {
            Assert.True(e.SizeBytes > 100_000_000, $"{e.Id} has no plausible size");
            Assert.EndsWith(".gguf", e.File);                       // models are only non-executable weight formats (NFR-13)
        }
        Assert.Equal(manifest.Entries.Count, manifest.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(manifest.Entries.Count, manifest.Entries.Select(e => e.File).Distinct().Count());
        Assert.Equal(manifest.Entries.Count, manifest.Entries.Select(e => e.Sha256).Distinct().Count());
    }

    [Fact]
    public void Manifest_HasEmbeddingModelAndALanguageModelPerTier()
    {
        var manifest = ModelManifest.LoadDefault();

        Assert.Equal("Qwen3-Embedding-0.6B-Q8_0.gguf", manifest.Embedding.File);
        Assert.Null(manifest.Embedding.Tier);
        Assert.Equal(["qwen3-embedding-0.6b-q8_0", "qwen3.5-2b-q4_k_m"], manifest.ForTier(ModelTier.Minimum).Select(e => e.Id));
        Assert.Equal(["qwen3-embedding-0.6b-q8_0", "qwen3.5-4b-q4_k_m"], manifest.ForTier(ModelTier.Recommended).Select(e => e.Id));
        Assert.Equal(manifest.LlmFor(ModelTier.Recommended), manifest.LlmFor(ModelTier.Enhanced));   // falls back until GPU support
    }

    [Fact]
    public void Manifest_HasOnePinnedLlamaCppRuntimeFromTheOfficialReleases()
    {
        var manifest = ModelManifest.LoadDefault();
        var runtime = manifest.Runtime;                              // exactly one

        Assert.Equal(ModelRole.Runtime, runtime.Role);
        Assert.Null(runtime.Tier);
        Assert.Matches(@"^llama-b\d+-bin-win-cpu-x64\.zip$", runtime.File);
        var build = Regex.Match(runtime.File, @"b\d+").Value;
        Assert.Equal($"https://github.com/ggml-org/llama.cpp/releases/download/{build}/{runtime.File}", runtime.Url);   // only from the project's own releases
        Assert.Equal($"llama-server-{build}", runtime.Id);
        Assert.InRange(runtime.SizeBytes, 5_000_000, 100_000_000);
        Assert.Equal([runtime.Id, manifest.Embedding.Id, manifest.LlmFor(ModelTier.Minimum).Id], manifest.RequiredFor(ModelTier.Minimum).Select(e => e.Id));
        Assert.DoesNotContain(runtime, manifest.ForTier(ModelTier.Recommended));   // the model-only list stays models only
    }

    [Fact]
    public void Manifest_FindsModelsByHashRegardlessOfCase()
    {
        var manifest = ModelManifest.LoadDefault();
        var llm = manifest.LlmFor(ModelTier.Minimum);

        Assert.Equal(llm, manifest.FindBySha256(llm.Sha256.ToUpperInvariant()));
        Assert.Null(manifest.FindBySha256(new string('0', 64)));
    }

    [Fact]
    public void ChatContext_Recommendation_FollowsTheSameRamLineAsTheModel()
    {
        Assert.Equal(HardwareTiers.LargeChatContext, HardwareTiers.SelectChatContext(12 * Gb));
        Assert.Equal(HardwareTiers.StandardChatContext, HardwareTiers.SelectChatContext(12 * Gb - 1));
        Assert.Equal(HardwareTiers.StandardChatContext, HardwareTiers.SelectChatContext(4 * Gb));
    }

    [Fact]
    public void ServerArguments_ChatContext_IsPassedAndSlotsKeepLlamaServersDefault()
    {
        var entry = Entry(RandomBytes(10));

        var args = LlamaServerProcess.BuildArguments(entry.ToServerOptions("llama-server", Store(), 4, 8192), 8080, "key");

        Assert.Equal("8192", args[args.ToList().IndexOf("-c") + 1]);
        Assert.DoesNotContain("-np", args);              // auto slots sharing one pool (`--help` of build b11430)
        Assert.DoesNotContain("--kv-unified", args);
    }

    [Fact]
    public void ModelEntry_ServerOptions_CarryTheManifestFlags()
    {
        var data = RandomBytes(10);
        var entry = Entry(data) with { ServerArgs = ["--reasoning-budget", "0"] };

        var args = LlamaServerProcess.BuildArguments(entry.ToServerOptions("llama-server", Store(), 4), 8080, "key");

        Assert.Equal(Path.Combine(_dir, "models", "tiny.gguf"), args[args.ToList().IndexOf("-m") + 1]);
        Assert.Equal(["--reasoning-budget", "0"], args.TakeLast(2));
        Assert.DoesNotContain("--embedding", args);
    }

    // ---- download -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_FreshFile_IsVerifiedAndRenamed()
    {
        var data = RandomBytes(3_000_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data);
        var events = new List<DownloadProgress>();

        var path = await new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store, new SyncProgress<DownloadProgress>(events.Add));

        Assert.Equal(store.PathFor(entry), path);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(store.PartPathFor(entry)));
        Assert.Equal([null], host.RangeHeaders);                         // a plain download sends no Range header
        // Progress rises monotonically to the full size, then a verification pass.
        var downloading = events.Where(e => !e.Verifying).Select(e => e.BytesDone).ToList();
        Assert.Equal(downloading.Order(), downloading);
        Assert.Equal(data.Length, downloading[^1]);
        Assert.Contains(events, e => e.Verifying);
        Assert.All(events, e => Assert.Equal(data.Length, e.BytesTotal));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public async Task Download_Resume_ContinuesFromPartialFile()
    {
        var data = RandomBytes(3_000_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data);
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllBytesAsync(store.PartPathFor(entry), data[..1_200_000]);

        var path = await new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store);

        Assert.Equal(["bytes=1200000-"], host.RangeHeaders);
        Assert.Equal(data.Length - 1_200_000, host.BytesServed);          // only the missing part was transferred
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(store.PartPathFor(entry)));
    }

    [Fact]
    public async Task Download_InterruptedByTheNetwork_ResumesOnRetry()
    {
        var data = RandomBytes(3_000_000);
        var host = new FakeFileHost(data, dropAfterBytes: 1_000_000);
        var downloader = new ModelDownloader(new HttpClient(host));
        var store = Store();
        var entry = Entry(data);

        await Assert.ThrowsAnyAsync<IOException>(() => downloader.DownloadAsync(entry, store));
        var partial = new FileInfo(store.PartPathFor(entry)).Length;
        Assert.InRange(partial, 1, 1_000_000);                            // what arrived was kept
        Assert.False(File.Exists(store.PathFor(entry)));

        host.DropAfterBytes = null;                                       // the network is back
        var path = await downloader.DownloadAsync(entry, store);

        Assert.Equal($"bytes={partial}-", host.RangeHeaders[1]);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Download_ServerIgnoresRange_StartsOverWithoutCorruption()
    {
        var data = RandomBytes(2_000_000);
        var host = new FakeFileHost(data, supportsRange: false);
        var store = Store();
        var entry = Entry(data);
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllBytesAsync(store.PartPathFor(entry), data[..500_000]);

        var path = await new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store);

        Assert.Equal(data.Length, host.BytesServed);                      // the whole file came again
        Assert.Equal(data, await File.ReadAllBytesAsync(path));           // and was not appended to the old partial data
    }

    [Fact]
    public async Task Download_PartialFileLargerThanExpected_IsDiscarded()
    {
        var data = RandomBytes(1_000_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data);
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllBytesAsync(store.PartPathFor(entry), RandomBytes(2_000_000, seed: 9));

        var path = await new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store);

        Assert.Equal([null], host.RangeHeaders);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Download_HashMismatch_DeletesFile()
    {
        var data = RandomBytes(1_500_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data, hash: new string('a', 64));               // the manifest pins a different file

        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() => new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store));

        Assert.Equal(Sha(data), ex.Actual);
        Assert.False(File.Exists(store.PathFor(entry)));
        Assert.False(File.Exists(store.PartPathFor(entry)));              // nothing is left to resume from
    }

    [Fact]
    public async Task Download_AlreadyInstalled_DoesNotTouchTheNetwork()
    {
        var data = RandomBytes(1_000_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data);
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllBytesAsync(store.PathFor(entry), data);

        await new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store);

        Assert.Equal(0, host.Requests);
    }

    [Fact]
    public async Task Download_CorruptInstalledFile_IsFetchedAgain()
    {
        var data = RandomBytes(1_000_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data);
        Directory.CreateDirectory(store.Folder);
        var damaged = (byte[])data.Clone();
        damaged[500_000] ^= 0xFF;                                         // one flipped byte
        await File.WriteAllBytesAsync(store.PathFor(entry), damaged);
        Assert.Equal(ModelState.Corrupt, await store.CheckAsync(entry));

        await new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store);

        Assert.Equal(ModelState.Installed, await store.CheckAsync(entry));
        Assert.Equal(1, host.Requests);
    }

    [Fact]
    public async Task Download_Cancelled_KeepsThePartialFileForResume()
    {
        var data = RandomBytes(3_000_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<DownloadProgress>(p => { if (p.BytesDone >= 1_000_000) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store, progress, cts.Token));

        Assert.True(File.Exists(store.PartPathFor(entry)));
        Assert.False(File.Exists(store.PathFor(entry)));
        Assert.Equal(ModelState.Partial, await store.CheckAsync(entry));
    }

    [Fact]
    public async Task Download_RangeNotSatisfiable_RestartsOnceThenReportsAnIncompleteFile()
    {
        // The host's file is shorter than the manifest says (it was replaced), so our resume offset is past its end: 416.
        var data = RandomBytes(600_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data) with { SizeBytes = 1_000_000 };
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllBytesAsync(store.PartPathFor(entry), RandomBytes(800_000, seed: 5));

        var ex = await Assert.ThrowsAsync<IOException>(() => new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store));

        Assert.Equal(["bytes=800000-", null], host.RangeHeaders);           // asked to resume, was refused, started over
        Assert.Contains("ended after 600000 of 1000000 bytes", ex.Message);
        Assert.False(File.Exists(store.PathFor(entry)));                    // never installed
    }

    [Fact]
    public async Task Download_AHostThatSendsMoreThanTheManifestSize_IsStoppedBeforeWritingPastIt()
    {
        var data = RandomBytes(600_000);
        var host = new FakeFileHost(data);
        var store = Store();
        var entry = Entry(data) with { SizeBytes = 400_000 };

        var ex = await Assert.ThrowsAsync<IOException>(() => new ModelDownloader(new HttpClient(host)).DownloadAsync(entry, store));

        Assert.Contains("larger than the 400000 bytes", ex.Message);
        Assert.True(!File.Exists(store.PartPathFor(entry)) || new FileInfo(store.PartPathFor(entry)).Length <= 400_000);
        Assert.False(File.Exists(store.PathFor(entry)));                    // never installed
    }

    // ---- check state --------------------------------------------------------------------------------------

    [Fact]
    public async Task Store_CheckAsync_ReportsEachState()
    {
        var data = RandomBytes(100_000);
        var store = Store();
        var entry = Entry(data);
        Directory.CreateDirectory(store.Folder);

        Assert.Equal(ModelState.Missing, await store.CheckAsync(entry));
        await File.WriteAllBytesAsync(store.PartPathFor(entry), data[..10]);
        Assert.Equal(ModelState.Partial, await store.CheckAsync(entry));
        await File.WriteAllBytesAsync(store.PathFor(entry), data[..50_000]);              // wrong size
        Assert.Equal(ModelState.Corrupt, await store.CheckAsync(entry));
        await File.WriteAllBytesAsync(store.PathFor(entry), data);
        Assert.Equal(ModelState.Installed, await store.CheckAsync(entry));
    }

    // ---- import -------------------------------------------------------------------------------------------

    private static ModelManifest ManifestOf(params ModelEntry[] entries) => new(entries);

    [Fact]
    public async Task Import_KnownHash_IsCopied()
    {
        var data = RandomBytes(500_000);
        var entry = Entry(data, file: "official-name.gguf");
        var source = Path.Combine(_dir, "downloads", "renamed-by-user.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, data);
        var store = Store();

        var imported = await ModelImporter.ImportAsync(source, ManifestOf(entry), store);

        Assert.Equal(entry, imported);
        Assert.Equal(data, await File.ReadAllBytesAsync(store.PathFor(entry)));            // stored under the manifest's name
        Assert.True(File.Exists(source));                                                  // the original is left alone
        Assert.False(File.Exists(store.PartPathFor(entry)));
    }

    [Fact]
    public async Task Import_UnknownHash_IsRejected()
    {
        var data = RandomBytes(500_000);
        var source = Path.Combine(_dir, "mystery.gguf");
        await File.WriteAllBytesAsync(source, RandomBytes(500_000, seed: 77));
        var store = Store();

        var ex = await Assert.ThrowsAsync<ModelRejectedException>(() => ModelImporter.ImportAsync(source, ManifestOf(Entry(data)), store));

        Assert.Contains("not one of the models", ex.Message);
        Assert.False(Directory.Exists(store.Folder) && Directory.GetFiles(store.Folder).Length > 0);
    }

    [Theory]
    [InlineData("model.exe")]
    [InlineData("model.bin")]
    [InlineData("model.pt")]
    [InlineData("model.dll")]
    public async Task Import_ExecutableOrUnsafeFormats_AreRejectedBeforeAnythingIsRead(string name)
    {
        var data = RandomBytes(1000);
        var source = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(source, data);

        var ex = await Assert.ThrowsAsync<ModelRejectedException>(() => ModelImporter.ImportAsync(source, ManifestOf(Entry(data)), Store()));

        Assert.Contains(".gguf", ex.Message);
    }

    [Theory]
    [InlineData("model.GGUF")]
    [InlineData("model.onnx")]
    [InlineData("model.safetensors")]
    [InlineData("llama.zip")]                 // the pinned llama.cpp zip; accepted only when its hash is in the manifest
    public async Task Import_AcceptsTheNonExecutableWeightFormats(string name)
    {
        var data = RandomBytes(1000);
        var source = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(source, data);

        var entry = await ModelImporter.ImportAsync(source, ManifestOf(Entry(data, file: "x.gguf")), Store());

        Assert.Equal("x.gguf", entry.File);
    }

    // ---- architecture -------------------------------------------------------------------------------------

    [Fact]
    public void Architecture_OnlyModelsProject_ReferencesHttpClient()
    {
        static bool UsesHttp(Assembly a) => a.GetReferencedAssemblies().Any(r => r.Name!.StartsWith("System.Net.Http", StringComparison.Ordinal));

        // The libraries the app and worker build on must not make network calls; only BookSearch.Models may.
        Assert.False(UsesHttp(typeof(Database).Assembly), "BookSearch.Core must not use System.Net.Http");
        Assert.False(UsesHttp(typeof(Extractor).Assembly), "BookSearch.Parsing must not use System.Net.Http");
        Assert.True(UsesHttp(typeof(ModelDownloader).Assembly), "BookSearch.Models is where HTTP lives");
    }
}
