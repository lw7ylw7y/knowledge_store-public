using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookSearch.Models.Management;

/// <summary>Runtime is the llama.cpp program itself (a zip), not model weights.</summary>
public enum ModelRole { Embedding, Llm, Runtime }

/// <summary>Hardware tiers (Requirements, Non-functional). The enhanced tier needs a GPU and arrives with GPU support.</summary>
public enum ModelTier { Minimum, Recommended, Enhanced }

/// <param name="Tier">The tier this model is for; null for models used on every tier (the embedding model).</param>
/// <param name="Sha256">The pinned hash of the file, checked after every download and import.</param>
/// <param name="ServerArgs">Extra llama-server flags for this model.</param>
public sealed record ModelEntry(
    string Id,
    ModelRole Role,
    ModelTier? Tier,
    string File,
    string Url,
    string Sha256,
    long SizeBytes,
    string License,
    string Source,
    IReadOnlyList<string> ServerArgs)
{
    /// <summary>Launch options for this model: the manifest's flags are added to the standard ones.</summary>
    public LlamaServerOptions ToServerOptions(string serverExe, ModelStore store, int threads, int contextSize = 0) =>
        new(serverExe, store.PathFor(this), Role == ModelRole.Embedding ? ServerRole.Embedding : ServerRole.Chat,
            threads, contextSize, ServerArgs);
}

/// <summary>The models the app may download or import: file, source, size, licence and pinned hash for each.</summary>
public sealed class ModelManifest(IReadOnlyList<ModelEntry> entries)
{
    private sealed record Document(int Version, List<ModelEntry> Models);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<ModelEntry> Entries { get; } = entries;

    public static ModelManifest LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BookSearch.Models.models.manifest.json")
                           ?? throw new InvalidOperationException("The model manifest is missing from the assembly.");
        return Parse(new StreamReader(stream).ReadToEnd());
    }

    public static ModelManifest Load(string path) => Parse(System.IO.File.ReadAllText(path));

    private static ModelManifest Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidDataException("The model manifest is empty.");
        return new ModelManifest(doc.Models);
    }

    /// <summary>The pinned llama.cpp build the app downloads (or that the user imports as a zip).</summary>
    public ModelEntry Runtime => Entries.Single(e => e.Role == ModelRole.Runtime);

    public ModelEntry Embedding => Entries.Single(e => e.Role == ModelRole.Embedding);

    /// <summary>The language model for a tier. The enhanced tier falls back to the recommended model until GPU support exists.</summary>
    public ModelEntry LlmFor(ModelTier tier) =>
        Entries.FirstOrDefault(e => e.Role == ModelRole.Llm && e.Tier == tier)
        ?? Entries.First(e => e.Role == ModelRole.Llm && e.Tier == ModelTier.Recommended);

    /// <summary>The embedding model plus the language model for the tier.</summary>
    public IReadOnlyList<ModelEntry> ForTier(ModelTier tier) => [Embedding, LlmFor(tier)];

    /// <summary>Everything the app needs on a tier: the llama.cpp program and the two models.</summary>
    public IReadOnlyList<ModelEntry> RequiredFor(ModelTier tier) => [Runtime, Embedding, LlmFor(tier)];

    public ModelEntry? FindBySha256(string sha256) =>
        Entries.FirstOrDefault(e => e.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));
}
