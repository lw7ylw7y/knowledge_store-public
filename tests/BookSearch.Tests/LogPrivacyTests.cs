using BookSearch.App;
using BookSearch.Core;
using BookSearch.Core.Data;
using BookSearch.Core.Search;
using Microsoft.Data.Sqlite;

namespace BookSearch.Tests;

/// <summary>NFR-16: logs and crash reports contain no book text or questions.</summary>
public class LogPrivacyTests
{
    private const string Secret = "my private question about grandmother's recipe";

    [Fact]
    public void Describe_DatabaseError_DoesNotQuoteTheSearchTerms()
    {
        using var db = new TestDb();
        var ex = Assert.ThrowsAny<SqliteException>(() =>
            new SqliteCommand("SELECT rowid FROM chunk_fts WHERE chunk_fts MATCH 'grandmother:recipe'", db.Conn).ExecuteScalar());   // "no such column: grandmother"
        Assert.Contains("grandmother", ex.Message);                                  // the raw message does quote the terms

        var text = ExceptionDescriber.Describe(ex);

        Assert.DoesNotContain("grandmother", text);
        Assert.StartsWith("Microsoft.Data.Sqlite.SqliteException: SQLite error", text);
    }

    [Fact]
    public void Describe_ModelServerLogLines_AreCutOff()
    {
        var ex = new ModelUnavailableException($"llama-server did not become ready.{Environment.NewLine}slot 0 | prompt = {Secret}{Environment.NewLine}more");

        var text = ExceptionDescriber.Describe(ex);

        Assert.Contains("llama-server did not become ready.", text);
        Assert.DoesNotContain(Secret, text);
    }

    [Fact]
    public void Describe_ModelServerReplyBody_IsCutOffAfterTheStatus()
    {
        var ex = new ModelUnavailableException($"llama-server embedding request failed: 400 {{\"error\":\"bad input: {Secret}\"}}");

        var text = ExceptionDescriber.Describe(ex);

        Assert.Contains("embedding request failed: 400", text);
        Assert.DoesNotContain(Secret, text);
    }

    [Fact]
    public void Describe_OtherMessages_KeepOnlyTheirFirstLineAndALimitedLength()
    {
        var ex = new InvalidOperationException("Could not read the file.\nsecond line: " + Secret);
        var long_ = new InvalidOperationException(new string('x', 500));

        Assert.Contains("Could not read the file.", ExceptionDescriber.Describe(ex));
        Assert.DoesNotContain(Secret, ExceptionDescriber.Describe(ex));
        Assert.True(ExceptionDescriber.SafeMessage(long_).Length <= 201);
    }

    [Fact]
    public void Describe_KeepsTheTypeAndTheHresult()
    {
        var text = ExceptionDescriber.Describe(new System.Runtime.InteropServices.COMException("", unchecked((int)0x80004005)));

        Assert.Contains("COMException", text);
        Assert.Contains("0x80004005", text);                                         // an empty message: the code identifies it
    }

    [Fact]
    public void AppCode_WritesNoFilesOtherThanTheCrashLog()
    {
        // The only place the app writes under logs\\ is CrashLog (exception type, message, stack): a quick guard against a new log file.
        var root = AppRoot();
        var writers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains("bookcli"))
            .Where(f => File.ReadAllText(f).Contains("LogFolder"))
            .Select(Path.GetFileName).Order().ToList();

        Assert.Equal(["AppPaths.cs", "CrashLog.cs", "DataEraser.cs"], writers);       // the path, the writer, and the eraser
    }

    private static string AppRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "BookSearch.App"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found");
    }
}
