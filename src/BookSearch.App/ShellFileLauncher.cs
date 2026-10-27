using System.ComponentModel;
using System.Diagnostics;
using BookSearch.App.Reader;

namespace BookSearch.App;

/// <summary>Opens a book in whatever program Windows has for its file type.</summary>
internal sealed class ShellFileLauncher : IFileLauncher
{
    public bool Exists(string path) => File.Exists(path);

    public void Open(string path)
    {
        // Only book files: a stored path can come from anywhere, and ShellExecute would run an .exe or .lnk.
        if (!LibraryViewModel.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new IOException("Only EPUB, PDF, TXT and MD files can be opened.");
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            throw new IOException(e.Message, e);   // for example: no program is associated with this file type
        }
    }
}
