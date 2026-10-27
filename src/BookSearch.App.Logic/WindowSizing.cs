namespace BookSearch.App;

/// <summary>
/// The window sizes of the UX design (NFR-22: the smallest supported window is 800 x 600), in device-independent pixels. The
/// window multiplies them by the display scale.
/// </summary>
public static class WindowSizing
{
    public const int MinWidth = 800;
    public const int MinHeight = 600;
    public const int DefaultWidth = 1100;
    public const int DefaultHeight = 750;
}
