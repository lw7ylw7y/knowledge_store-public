namespace BookSearch.App;

/// <summary>Writes unhandled errors to logs\crash.log: exception type, message and stack only, never book text or questions (NFR-16).</summary>
internal static class CrashLog
{
    public static void Write(Exception e)
    {
        try
        {
            var paths = AppPaths.Default();
            Directory.CreateDirectory(paths.LogFolder);
            File.AppendAllText(Path.Combine(paths.LogFolder, "crash.log"),
                $"{DateTime.UtcNow:O} {Describe(e)}{Environment.NewLine}{e.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { /* nothing more we can do */ }
    }

    /// <summary>A one-line note in the same file for something non-fatal that is worth knowing about. Never put user content in it.</summary>
    public static void Note(string line)
    {
        try
        {
            var paths = AppPaths.Default();
            Directory.CreateDirectory(paths.LogFolder);
            File.AppendAllText(Path.Combine(paths.LogFolder, "crash.log"), $"{DateTime.UtcNow:O} {line}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { /* nothing more we can do */ }
    }

    /// <summary>Type, message and HRESULT (a COMException's message is often empty, so the code is what identifies it). Messages that could quote a question or book text are cut down first (<see cref="ExceptionDescriber"/>).</summary>
    public static string Describe(Exception e) => ExceptionDescriber.Describe(e);
}
