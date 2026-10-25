using System.Runtime.InteropServices;

namespace ShadeNet.Setup;

/// <summary>Tells Windows Explorer that the program's icon (and so its shortcuts' pictures) may have changed.</summary>
static class ShellIcons
{
    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const int SHCNE_UPDATEITEM = 0x00002000;
    const uint SHCNF_PATHW = 0x0005;
    const uint SHCNF_FLUSH = 0x1000;

    /// <summary>Where the taskbar and the Start menu keep the shortcuts a user pinned.</summary>
    static IEnumerable<string> PinnedFolders()
    {
        var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned");
        yield return Path.Combine(pinned, "TaskBar");
        yield return Path.Combine(pinned, "StartMenu");
    }

    /// <summary>
    /// The shortcuts the user pinned that start <paramref name="target"/>. They are not ours to rewrite (Windows keeps its own
    /// settings in them), but their time can be touched, which is enough for Explorer to read their icon again.
    /// </summary>
    public static List<string> PinnedShortcutsTo(string target)
    {
        var found = new List<string>();
        foreach (var folder in PinnedFolders())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var link in Directory.EnumerateFiles(folder, "*.lnk"))
                {
                    if (!string.Equals(Shortcuts.TargetOf(link), target, StringComparison.OrdinalIgnoreCase)) continue;
                    File.SetLastWriteTimeUtc(link, DateTime.UtcNow);
                    found.Add(link);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                // somebody else's folder: best effort
            }
        }

        return found;
    }

    /// <summary>Explorer reads the icons of these shortcuts again, and every icon it has cached for a program whose icon changed.</summary>
    public static void Notify(IReadOnlyList<string> changed)
    {
        foreach (var path in changed)
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, path, IntPtr.Zero);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
