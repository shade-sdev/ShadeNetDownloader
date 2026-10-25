using System.Runtime.InteropServices;

namespace ShadeNetDownloader.Services;

/// <summary>
/// The name Windows knows ShadeNet's taskbar button by (its AppUserModelID).
/// <para>
/// Without one, Windows derives it from the program's path and then takes the button's picture from the Start menu shortcut that starts
/// the program, not from the window. That picture is the one Explorer saw first, so the icon the user chooses in Settings never reached
/// the taskbar of the installed program (the debug build has no such shortcut and showed it), and an update that brought a new icon left
/// the old one there. With a name that no shortcut carries, the button shows the window's own icon, whatever it is now.
/// </para>
/// </summary>
public static class AppIdentity
{
    public const string TaskbarId = "ShadeNet.Downloader";

    /// <summary>Call before the first window is made. A failure only means the taskbar keeps its default behaviour.</summary>
    public static bool Set(string id = TaskbarId)
    {
        try
        {
            return SetCurrentProcessExplicitAppUserModelID(id) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return false;
        }
    }

    /// <summary>The name the running program has (for tests and checks).</summary>
    public static string? Current()
    {
        try
        {
            return GetCurrentProcessExplicitAppUserModelID(out var id) == 0 ? ReadAndFree(id) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    static string? ReadAndFree(IntPtr text)
    {
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);
}
