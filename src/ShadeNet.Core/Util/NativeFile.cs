using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShadeNet.Core.Util;

/// <summary>Windows-only file helpers (sparse files, Mark-of-the-Web). Every method is best-effort.</summary>
internal static class NativeFile
{
    const uint FSCTL_SET_SPARSE = 0x000900C4;

    [StructLayout(LayoutKind.Sequential)]
    struct FileSetSparseBuffer
    {
        public byte SetSparse;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        ref FileSetSparseBuffer lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// Marks the file sparse so that extending it to the full size does not make NTFS zero-fill the gap when a
    /// connection writes at a high offset.
    /// </summary>
    public static void TrySetSparse(SafeFileHandle handle, bool sparse)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var buffer = new FileSetSparseBuffer { SetSparse = sparse ? (byte)1 : (byte)0 };
            DeviceIoControl(handle, FSCTL_SET_SPARSE, ref buffer, Marshal.SizeOf<FileSetSparseBuffer>(),
                IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            // Not NTFS / not supported: preallocation simply behaves normally.
        }
    }

    /// <summary>Writes the Zone.Identifier alternate data stream so SmartScreen treats the file as downloaded from the internet.</summary>
    public static void TryWriteMarkOfTheWeb(string path, string? referrerUrl, string? hostUrl)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var text = "[ZoneTransfer]\r\nZoneId=3\r\n";
            if (!string.IsNullOrEmpty(referrerUrl)) text += $"ReferrerUrl={referrerUrl}\r\n";
            if (!string.IsNullOrEmpty(hostUrl)) text += $"HostUrl={hostUrl}\r\n";
            File.WriteAllText(path + ":Zone.Identifier", text);
        }
        catch
        {
            // FAT/exFAT volumes have no ADS.
        }
    }
}
