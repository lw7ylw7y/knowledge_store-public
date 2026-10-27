using BookSearch.Core.Data;

namespace BookSearch.App;

public sealed record LibraryStatus(int BookCount, string VecVersion, int SchemaVersion);

/// <summary>Opens and migrates library.db at start-up; failing here (for example if vec0.dll is missing) is shown to the user.</summary>
public static class LibraryDatabase
{
    public static LibraryStatus Initialize(AppPaths paths)
    {
        using var conn = Database.Open(paths.DatabasePath);
        Migrator.Migrate(conn);
        return new LibraryStatus(new BookRepository(conn).List().Count, Database.VecVersion(conn), Migrator.CurrentVersion(conn));
    }
}
