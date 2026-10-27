using BookSearch.Core;
using Microsoft.Data.Sqlite;

namespace BookSearch.App;

/// <summary>
/// Describes an exception for a log file or an on-screen error without risking book text or questions (NFR-16). Some
/// exception messages quote their input: a database error quotes the search terms, and a model-server error carries the
/// server's own log lines or reply. Those messages are cut down to what the app itself wrote.
/// </summary>
public static class ExceptionDescriber
{
    private const int MaxMessage = 200;

    /// <summary>"Type: message (HRESULT 0x...)", with the message made safe.</summary>
    public static string Describe(Exception e) => $"{e.GetType().FullName}: {SafeMessage(e)} (HRESULT 0x{e.HResult:X8})";

    public static string SafeMessage(Exception e)
    {
        // A database error says which term it choked on ("fts5: syntax error near ...").
        if (e is SqliteException sqlite) return $"SQLite error {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}";

        var message = e.Message ?? "";
        // A model-server failure appends the server's log lines or its reply body after the first line, and the reply
        // can echo the text that was sent ("llama-server embedding request failed: 400 {...}").
        if (e is ModelUnavailableException)
        {
            var status = System.Text.RegularExpressions.Regex.Match(message, @"request failed: \d{3}");
            if (status.Success) return message[..(status.Index + status.Length)];
        }

        // Anything else: the first line only, and not too long.
        var firstLine = message.Split('\r', '\n')[0];
        return firstLine.Length <= MaxMessage ? firstLine : firstLine[..MaxMessage] + "…";
    }
}
