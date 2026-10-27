using System.Runtime.InteropServices;

namespace BookSearch.App;

/// <summary>
/// The classic Windows "Open" dialog (comdlg32). Unlike the WinRT FileOpenPicker it needs no broker or app identity, so it
/// works whether or not the app runs elevated. It must be called on the UI thread and blocks it while the dialog is open.
/// </summary>
internal static class Win32FilePicker
{
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;
    private const int BufferChars = 32 * 1024;   // room for a few hundred long paths

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public string? FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    /// <param name="filterText">What the file-type box shows, for example "Books (EPUB, PDF, TXT, MD)".</param>
    /// <param name="patterns">Semicolon-separated wildcards, for example "*.epub;*.pdf".</param>
    /// <returns>The chosen files, or an empty list if the user cancelled.</returns>
    /// <exception cref="IOException">The dialog could not be shown (the error code is in the message).</exception>
    public static IReadOnlyList<string> Pick(IntPtr owner, string title, string filterText, string patterns)
    {
        var buffer = Marshal.AllocHGlobal(BufferChars * sizeof(char));
        try
        {
            Marshal.Copy(new char[BufferChars], 0, buffer, BufferChars);   // zeroed: an empty initial file name
            var ofn = new OpenFileName
            {
                Owner = owner,
                Filter = $"{filterText}\0{patterns}\0",   // the string's own terminator makes the required double null
                FilterIndex = 1,
                File = buffer,
                MaxFile = BufferChars,
                Title = title,
                Flags = OFN_EXPLORER | OFN_ALLOWMULTISELECT | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };
            ofn.StructSize = Marshal.SizeOf(ofn);

            if (!GetOpenFileName(ref ofn))
            {
                var error = CommDlgExtendedError();
                return error == 0 ? [] : throw new IOException($"The open-file dialog failed (error 0x{error:X}).");
            }
            return Parse(Read(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<string> Read(IntPtr buffer)
    {
        // "folder\0file1\0file2\0\0" for several files, or "full path\0\0" for one.
        var parts = new List<string>();
        var offset = 0;
        while (offset < BufferChars)
        {
            var text = Marshal.PtrToStringUni(buffer + offset * sizeof(char)) ?? "";
            if (text.Length == 0) break;
            parts.Add(text);
            offset += text.Length + 1;
        }
        return parts;
    }

    internal static IReadOnlyList<string> Parse(List<string> parts) =>
        parts.Count <= 1 ? parts : parts.Skip(1).Select(name => Path.Combine(parts[0], name)).ToList();
}
