using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShadeNetDownloader.Util;

/// <summary>The icon Windows shows for a file type (so a .mp4 looks like it does in Explorer), cached per extension.</summary>
public static class FileIconCache
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_LARGEICON = 0x0;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Returns a frozen image for the file name's extension, or null if Windows has none.</summary>
    public static ImageSource? Get(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        return Cache.GetOrAdd(ext, Load);
    }

    static ImageSource? Load(string ext)
    {
        try
        {
            var info = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
            if (SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
