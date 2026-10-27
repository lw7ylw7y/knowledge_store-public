namespace BookSearch.Models.Management;

/// <summary>
/// Installs a model file from disk for fully offline setup. Only weight formats that can't contain code are accepted
/// (NFR-13), and only files whose SHA-256 is in the manifest.
/// </summary>
public static class ModelImporter
{
    private static readonly string[] AllowedExtensions = [".gguf", ".onnx", ".safetensors", ".zip"];

    /// <exception cref="ModelRejectedException">Wrong file type, or not one of the models in the manifest.</exception>
    public static async Task<ModelEntry> ImportAsync(string sourcePath, ModelManifest manifest, ModelStore store,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            throw new ModelRejectedException($"Only {string.Join(", ", AllowedExtensions)} model files can be imported, not '{extension}'.", ModelRejection.WrongFileType);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Model file not found.", sourcePath);

        var size = new FileInfo(sourcePath).Length;
        var hash = await ModelStore.Sha256Async(sourcePath, done => progress?.Report(new DownloadProgress(Path.GetFileName(sourcePath), done, size, true)), ct)
            .ConfigureAwait(false);
        var entry = manifest.FindBySha256(hash)
                    ?? throw new ModelRejectedException("This file is not one of the models this app uses (its checksum is not in the model list), so it was not imported.", ModelRejection.UnknownModel);

        Directory.CreateDirectory(store.Folder);
        var part = store.PartPathFor(entry);
        File.Copy(sourcePath, part, overwrite: true);       // copy to a temporary name so a half-copied file is never "installed"
        File.Move(part, store.PathFor(entry), overwrite: true);
        return entry;
    }
}
