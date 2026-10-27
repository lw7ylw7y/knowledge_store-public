using System.Text.Json;

namespace BookSearch.Models;

/// <summary>
/// Hand-edited settings for the console tools (M0): where the llama.cpp server and model files are.
/// Model downloads and the manifest replace this in ST-09.
/// </summary>
public sealed record M0Config
{
    public required string ServerExe { get; init; }
    public required string EmbeddingModel { get; init; }
    public string? LlmModel { get; init; }
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    public const string SampleJson = """
        {
          "serverExe": "C:\\BookSearch\\bin\\llama-server.exe",
          "embeddingModel": "C:\\BookSearch\\models\\Qwen3-Embedding-0.6B-Q8_0.gguf",
          "llmModel": "C:\\BookSearch\\models\\Qwen3.5-4B-Q4_K_M.gguf",
          "threads": 4
        }
        """;

    public static M0Config Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Config not found: {path}\nCreate it with the paths to llama-server and the model files, for example:\n{SampleJson}", path);
        var config = JsonSerializer.Deserialize<M0Config>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException($"{path} is empty.");
        foreach (var (name, file) in new[] { ("serverExe", config.ServerExe), ("embeddingModel", config.EmbeddingModel) })
            if (!File.Exists(file)) throw new FileNotFoundException($"{name} points to a missing file: {file}", file);
        return config;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
