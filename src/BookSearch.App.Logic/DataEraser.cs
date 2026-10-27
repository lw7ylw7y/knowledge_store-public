using Microsoft.Data.Sqlite;

namespace BookSearch.App;

/// <summary>What "Delete all data" removes (ST-18).</summary>
public interface IDataEraser
{
    /// <summary>
    /// Deletes the library database (books, chunks, vectors and the search history), the logs and settings.json, and the
    /// downloaded models too when asked. Nothing else in the data folder is touched. Every process using the files must be
    /// stopped first.
    /// </summary>
    void Erase(bool deleteModels);
}

public sealed class DataEraser(AppPaths paths) : IDataEraser
{
    public void Erase(bool deleteModels)
    {
        SqliteConnection.ClearAllPools();   // pooled connections keep the file open on Windows
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) DeleteFile(paths.DatabasePath + suffix);
        DeleteFile(paths.SettingsPath);     // the wizard then runs again
        if (Directory.Exists(paths.LogFolder)) Directory.Delete(paths.LogFolder, recursive: true);
        if (deleteModels && Directory.Exists(paths.ModelsFolder)) Directory.Delete(paths.ModelsFolder, recursive: true);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
