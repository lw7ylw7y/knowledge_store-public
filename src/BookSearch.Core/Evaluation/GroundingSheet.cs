using System.Text;

namespace BookSearch.Core.Evaluation;

/// <param name="Supported">"y" or "n" once checked by hand; empty until then. Uncited sentences start as "n".</param>
public sealed record GroundingRow(
    string QuestionId, string Question, int Sentence, string SentenceText, string Citations, string Passages, string Supported);

public sealed record GroundingScore(int Supported, int Unsupported, int Unchecked)
{
    public int Checked => Supported + Unsupported;
    public double Share => Checked == 0 ? 0 : Supported / (double)Checked;
    public const double Target = 0.90;
    public bool MeetsTarget => Unchecked == 0 && Checked > 0 && Share >= Target;
}

/// <summary>
/// A CSV for hand-checking summaries (NFR-8): one row per summary sentence with the passages it cites, and a
/// Supported column to fill in with y or n. Scoring reads it back.
/// </summary>
public static class GroundingSheet
{
    private static readonly string[] Header = ["question_id", "question", "sentence", "sentence_text", "citations", "cited_passages", "supported (y/n)"];

    public static void Write(string path, IEnumerable<GroundingRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Quote)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", new[]
            {
                r.QuestionId, r.Question, r.Sentence.ToString(), r.SentenceText, r.Citations, r.Passages, r.Supported,
            }.Select(Quote)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel reads the Chinese and Japanese text
    }

    public static IReadOnlyList<GroundingRow> Read(string path)
    {
        var records = ParseCsv(File.ReadAllText(path)).ToList();
        if (records.Count == 0) return [];
        return records.Skip(1).Where(r => r.Count >= 7).Select(r =>
            new GroundingRow(r[0], r[1], int.TryParse(r[2], out var n) ? n : 0, r[3], r[4], r[5], r[6].Trim())).ToList();
    }

    public static GroundingScore Score(IEnumerable<GroundingRow> rows)
    {
        int yes = 0, no = 0, blank = 0;
        foreach (var r in rows)
            switch (r.Supported.Trim().ToLowerInvariant())
            {
                case "y" or "yes": yes++; break;
                case "n" or "no": no++; break;
                default: blank++; break;
            }
        return new GroundingScore(yes, no, blank);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static IEnumerable<List<string>> ParseCsv(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) yield return row;
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
