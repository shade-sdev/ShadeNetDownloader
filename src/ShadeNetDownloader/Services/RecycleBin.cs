using System.IO;
using System.Runtime.InteropServices;

namespace ShadeNetDownloader.Services;

/// <summary>Sends files to the Recycle Bin (Windows' own file operation: the user can still restore them), without asking.</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    /// <summary>True when every file that was there went to the Recycle Bin.</summary>
    public static bool Send(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).ToList();
        if (files.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", files) + "\0\0",   // (a list ends with two nulls)
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !files.Any(File.Exists);
    }
}
