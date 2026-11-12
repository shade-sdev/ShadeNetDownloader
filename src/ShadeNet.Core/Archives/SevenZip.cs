using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ShadeNet.Core.Archives;

/// <summary>
/// 7-Zip (Igor Pavlov's <c>7z.exe</c> with its <c>7z.dll</c>), which unpacks finished archives: every RAR (also with a password and in
/// several parts), 7z, zip, tar and the rest. ShadeNet ships it (tools\7zip, from tools\get-tools.ps1) and keeps it up to date like
/// yt-dlp and ffmpeg; a 7-Zip installed on the computer is used when no other is there.
/// </summary>
public static partial class SevenZip
{
    const string Exe = "7z.exe";

    /// <summary>
    /// The 7-Zip to use: the one chosen in Settings (a file, or the folder that holds it), else an update ShadeNet downloaded, else the
    /// copy that came with the program, else an installed 7-Zip, else one on the PATH.
    /// </summary>
    public static string? Locate(string? configured, string? appFolder = null, string? pathVariable = null, string? userFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var chosen = configured.Trim().Trim('"');
            if (File.Exists(chosen)) return chosen;
            if (Directory.Exists(chosen) && File.Exists(Path.Combine(chosen, Exe))) return Path.Combine(chosen, Exe);
            return null;   // an explicit choice that does not exist is not silently replaced by another 7-Zip
        }

        if (!string.IsNullOrWhiteSpace(userFolder) && File.Exists(Path.Combine(userFolder, "7zip", Exe))) return Path.Combine(userFolder, "7zip", Exe);

        appFolder ??= AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(appFolder, "tools", "7zip", Exe), Path.Combine(appFolder, "7zip", Exe), Path.Combine(appFolder, Exe) })
            if (File.Exists(candidate)) return candidate;

        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var installed = Path.Combine(Environment.GetFolderPath(programs), "7-Zip", Exe);
            if (File.Exists(installed)) return installed;
        }

        foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), Exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry
            }
        }

        return null;
    }

    [GeneratedRegex(@"7-Zip(?:\s+\(\w+\))?\s+(?<v>\d+\.\d+)")]
    private static partial Regex Banner();

    /// <summary>"26.06", from the line 7-Zip starts with ("7-Zip 26.06 (x64) : Copyright …"); null when it does not start.</summary>
    public static async Task<string?> VersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(info)!;
            var text = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var m = Banner().Match(text);
            return m.Success ? m.Groups["v"].Value : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
