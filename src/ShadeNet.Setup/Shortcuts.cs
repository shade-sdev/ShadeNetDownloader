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
