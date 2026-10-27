namespace BookSearch.App;

/// <summary>
/// Finds <c>llama-server.exe</c>. The first-run wizard downloads the pinned build into <c>bin</c> under the data folder
/// (<see cref="RuntimeInstaller"/>); a build put there by hand is used as it is. A copy in <c>bin</c> next to the app (a
/// developer's publish folder) is the fallback.
/// </summary>
public static class ServerLocator
{
    public const string FileName = "llama-server.exe";

    public static IReadOnlyList<string> Candidates(AppPaths paths, string appFolder) =>
    [
        Path.Combine(paths.DataFolder, "bin", FileName),
        Path.Combine(appFolder, "bin", FileName),
    ];

    public static string? Find(AppPaths paths, string appFolder) =>
        Candidates(paths, appFolder).FirstOrDefault(File.Exists);
}
