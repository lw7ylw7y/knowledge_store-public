using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookSearch.Core.Evaluation;

/// <param name="Type">answerable, paraphrase, cross-language, keyword or out-of-library.</param>
/// <param name="ExpectedFile">The book file the answer is in; null for out-of-library questions.</param>
/// <param name="ExpectedContains">A hit is correct when its text contains any of these (case-insensitive).</param>
public sealed record EvalQuestion(
    string Id, string Type, string Language, string Question, string? ExpectedFile, IReadOnlyList<string> ExpectedContains)
{
    public bool IsOutOfLibrary => ExpectedFile is null;
}

public static class EvalQuestions
{
    private sealed record Document([property: JsonPropertyName("questions")] List<EvalQuestion> Questions);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<EvalQuestion> Load(string path)
    {
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options)
                       ?? throw new InvalidDataException($"{path} is empty.");
        var ids = document.Questions.GroupBy(q => q.Id).FirstOrDefault(g => g.Count() > 1);
        if (ids is not null) throw new InvalidDataException($"Duplicate question id '{ids.Key}' in {path}.");
        return document.Questions;
    }
}
