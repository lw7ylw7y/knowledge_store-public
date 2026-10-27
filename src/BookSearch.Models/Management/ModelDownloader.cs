using System.Net;
using System.Net.Http.Headers;

namespace BookSearch.Models.Management;

/// <summary>Progress of a download, or of the checksum pass that follows it.</summary>
public sealed record DownloadProgress(string ModelId, long BytesDone, long BytesTotal, bool Verifying);

/// <summary>
/// Downloads a model once, resumably: data goes to <c>file.part</c> and is only renamed after its SHA-256 matches the
/// manifest. This is the only place the app contacts the internet (apart from the optional update check).
/// </summary>
public sealed class ModelDownloader(HttpClient http)
{
    public ModelDownloader() : this(CreateHttpClient()) { }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BookSearch");
        return client;
    }

    /// <summary>Downloads the model if it isn't already installed and valid; returns its path.</summary>
    /// <exception cref="ChecksumMismatchException">The file arrived but isn't the pinned model; it has been deleted.</exception>
    /// <exception cref="HttpRequestException">The network failed; the partial file is kept so a retry resumes.</exception>
    public async Task<string> DownloadAsync(ModelEntry entry, ModelStore store, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(store.Folder);
        var dest = store.PathFor(entry);
        var part = store.PartPathFor(entry);

        if (File.Exists(dest))
        {
            if (await store.CheckAsync(entry, ct).ConfigureAwait(false) == ModelState.Installed)
            {
                progress?.Report(new DownloadProgress(entry.Id, entry.SizeBytes, entry.SizeBytes, false));
                return dest;
            }
            File.Delete(dest);                    // corrupt or truncated: fetch it again
        }

        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > entry.SizeBytes) { File.Delete(part); have = 0; }

        if (have < entry.SizeBytes)
            await FetchAsync(entry, part, have, progress, ct).ConfigureAwait(false);

        // Verify before the file gets its real name.
        progress?.Report(new DownloadProgress(entry.Id, 0, entry.SizeBytes, true));
        var actual = await ModelStore.Sha256Async(part, done => progress?.Report(new DownloadProgress(entry.Id, done, entry.SizeBytes, true)), ct)
            .ConfigureAwait(false);
        if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new ChecksumMismatchException(entry.File, entry.Sha256, actual);
        }
        File.Move(part, dest);
        return dest;
    }

    private async Task FetchAsync(ModelEntry entry, string part, long have, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, entry.Url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && attempt == 0)
            {
                File.Delete(part);                // the server doesn't accept our offset: start again from the beginning
                have = 0;
                continue;
            }
            response.EnsureSuccessStatusCode();

            // 206 with the offset we asked for means resume; anything else (a plain 200) means start over.
            var resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent
                          && response.Content.Headers.ContentRange?.From == have;
            if (!resumed) have = 0;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            var done = have;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (done + read > entry.SizeBytes)   // the pinned size is the most this file can be; never write past it
                    throw new IOException($"{entry.File} is larger than the {entry.SizeBytes} bytes the manifest lists.");
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    progress?.Report(new DownloadProgress(entry.Id, done, entry.SizeBytes, false));
                    lastReport = DateTime.UtcNow;
                }
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(entry.Id, done, entry.SizeBytes, false));

            if (done != entry.SizeBytes)
                throw new IOException($"{entry.File} ended after {done} of {entry.SizeBytes} bytes; run the download again to resume.");
            return;
        }
    }
}
