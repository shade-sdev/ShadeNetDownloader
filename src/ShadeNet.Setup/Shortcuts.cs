namespace ShadeNet.Setup;

/// <summary>Windows shortcuts (.lnk) through the shell's own scripting object (no extra package).</summary>
static class Shortcuts
{
    public static void Create(string linkPath, string target, string workingDirectory, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new SetupException("Windows cannot create shortcuts on this computer.", 7);
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = target;
            link.WorkingDirectory = workingDirectory;
            link.Description = description;
            link.IconLocation = target + ",0";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Saves a shortcut of ours again, so that Windows reads its icon again (an update that changes the program's icon otherwise
    /// leaves the old picture on the desktop and the taskbar: Explorer keeps what it has seen). Only a shortcut that still points at
    /// <paramref name="target"/> is touched, and only its icon is set (and only when it is the program's own or none): a name, a
    /// description or an icon the user changed stay as they are.
    /// </summary>
    /// <returns><c>true</c> when the shortcut was saved again.</returns>
    public static bool Refresh(string linkPath, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null || !File.Exists(linkPath)) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(linkPath);
            if (!string.Equals((string)link.TargetPath, target, StringComparison.OrdinalIgnoreCase)) return false;

            var icon = (string)link.IconLocation;
            var own = string.IsNullOrWhiteSpace(icon) || icon.TrimStart(',').Length == 0 || icon.StartsWith(target, StringComparison.OrdinalIgnoreCase);
            if (own) link.IconLocation = target + ",0";
            link.Save();
            return true;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Where a shortcut points (for tests and checks).</summary>
    public static string? TargetOf(string linkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null || !File.Exists(linkPath)) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            return (string)shell.CreateShortcut(linkPath).TargetPath;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
