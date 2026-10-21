using System.Diagnostics;
using System.Text;

namespace ShadeNet.Core.Hls;

/// <summary>Finds and runs ffmpeg, which joins the downloaded pieces into one video file without re-encoding (<c>-c copy</c>).</summary>
public static class Ffmpeg
{
    /// <summary>
    /// The ffmpeg to use: the one the user chose (a file, or a folder that holds it), one next to the program (also in a
    /// <c>tools\ffmpeg</c> folder), or one on the PATH. <c>null</c> when there is none.
    /// </summary>
    public static string? Locate(string? configured, string? appFolder = null, string? pathVariable = null, string? userFolder = null)
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var chosen = configured.Trim().Trim('"');
            if (File.Exists(chosen)) return chosen;
            if (Directory.Exists(chosen))
            {
                foreach (var candidate in new[] { Path.Combine(chosen, exe), Path.Combine(chosen, "bin", exe) })
                    if (File.Exists(candidate)) return candidate;
            }

            return null;   // an explicit choice that does not exist is not silently replaced by another ffmpeg
        }

        // a copy ShadeNet downloaded as an update (in the user's own folder) comes before the one that was installed with the program
        if (!string.IsNullOrWhiteSpace(userFolder) && File.Exists(Path.Combine(userFolder, "ffmpeg", exe))) return Path.Combine(userFolder, "ffmpeg", exe);

        appFolder ??= AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(appFolder, exe),
                     Path.Combine(appFolder, "ffmpeg", exe),
                     Path.Combine(appFolder, "tools", "ffmpeg", exe),
                     Path.Combine(appFolder, "tools", "ffmpeg", "bin", exe),
                 })
            if (File.Exists(candidate)) return candidate;

        foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry
            }
        }

        return null;
    }

    /// <summary>Arguments that copy the given inputs (video first, then the separate audio) into one file.</summary>
    /// <param name="subtitles">WebVTT files to put inside the video (language as ffmpeg wants it, e.g. "eng").</param>
    public static IReadOnlyList<string> RemuxArguments(IReadOnlyList<string> inputs, string output, bool fastStart,
        IReadOnlyList<(string File, string Language)>? subtitles = null)
    {
        var subs = subtitles ?? Array.Empty<(string File, string Language)>();
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostdin" };
        foreach (var input in inputs.Concat(subs.Select(s => s.File)))
        {
            args.Add("-i");
            args.Add(input);
        }

        if (subs.Count > 0)
        {
            // everything of the media (or its picture + the second file's sound), then each subtitle file's track
            if (inputs.Count > 1) args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
            else args.AddRange(new[] { "-map", "0" });
            for (var i = 0; i < subs.Count; i++)
            {
                args.Add("-map");
                args.Add($"{inputs.Count + i}:0");
            }
        }
        else if (inputs.Count > 1)
        {
            // the picture of the first file, the sound of the second
            args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
        }

        args.AddRange(new[] { "-c", "copy" });
        if (subs.Count > 0)
        {
            args.AddRange(new[] { "-c:s", output.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "srt" : "mov_text" });
            for (var i = 0; i < subs.Count; i++) args.AddRange(new[] { $"-metadata:s:s:{i}", $"language={subs[i].Language}" });
        }

        if (fastStart && output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(output);
        return args;
    }

    /// <returns>The exit code and what ffmpeg wrote to its error output (the reason, when it failed).</returns>
    public static async Task<(int ExitCode, string Log)> RunAsync(string exe, IEnumerable<string> arguments, CancellationToken ct,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        foreach (var a in arguments) info.ArgumentList.Add(a);

        using var process = new Process { StartInfo = info };
        var log = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) { if (log.Length < 8000) log.AppendLine(e.Data); } };
        process.OutputDataReceived += (_, _) => { };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended by itself
            }

            throw;
        }

        process.WaitForExit();   // flushes the redirected output
        lock (log) return (process.ExitCode, log.ToString().Trim());
    }
}
