using System.Security.Cryptography;

namespace BookSearch.Models.Management;

public enum ModelState
{
    Missing,
    /// <summary>A partly downloaded <c>.part</c> file exists; a download will resume from it.</summary>
    Partial,
    Installed,
    /// <summary>The file is there but its size or hash doesn't match the manifest.</summary>
    Corrupt,
}

public sealed class ChecksumMismatchException(string file, string expected, string actual)
    : Exception($"{file} failed its checksum (expected {expected}, got {actual}).")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}

public enum ModelRejection { Other, WrongFileType, UnknownModel }

/// <param name="reason">Lets the app show its own translated sentence instead of <see cref="Exception.Message"/>.</param>
public sealed class ModelRejectedException(string message, ModelRejection reason = ModelRejection.Other) : Exception(message)
{
    public ModelRejection Reason { get; } = reason;
}

/// <summary>The folder where model files live (<c>models\</c> in the data folder).</summary>
public sealed class ModelStore(string folder)
{
    public string Folder { get; } = folder;

    public string PathFor(ModelEntry entry) => Path.Combine(Folder, entry.File);
    public string PartPathFor(ModelEntry entry) => PathFor(entry) + ".part";

    /// <summary>Checks the file against the manifest (size first, then the full hash).</summary>
    public async Task<ModelState> CheckAsync(ModelEntry entry, CancellationToken ct = default)
    {
        var path = PathFor(entry);
        if (!File.Exists(path)) return File.Exists(PartPathFor(entry)) ? ModelState.Partial : ModelState.Missing;
        if (new FileInfo(path).Length != entry.SizeBytes) return ModelState.Corrupt;
        return await Sha256Async(path, null, ct).ConfigureAwait(false) == entry.Sha256.ToLowerInvariant()
            ? ModelState.Installed
            : ModelState.Corrupt;
    }

    public static async Task<string> Sha256Async(string path, Action<long>? onProgress, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            onProgress?.Invoke(done);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
