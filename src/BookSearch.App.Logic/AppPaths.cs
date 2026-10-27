namespace BookSearch.App;

/// <summary>Where the app keeps its files. Everything lives under one per-user data folder.</summary>
public sealed class AppPaths(string dataFolder)
{
    public const string DataFolderVariable = "BOOKSEARCH_DATA";

    public string DataFolder { get; } = dataFolder;
    public string ModelsFolder => Path.Combine(DataFolder, "models");
    public string DatabasePath => Path.Combine(DataFolder, "library.db");
    public string SettingsPath => Path.Combine(DataFolder, "settings.json");
    public string LogFolder => Path.Combine(DataFolder, "logs");

    /// <summary>
    /// <c>%LOCALAPPDATA%\BookSearch</c> (the folder <c>bookcli</c> uses too), or the folder in the
    /// <c>BOOKSEARCH_DATA</c> environment variable, which gives a "fresh profile" for testing.
    /// </summary>
    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataFolderVariable);
        return new AppPaths(!string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookSearch"));
    }
}
