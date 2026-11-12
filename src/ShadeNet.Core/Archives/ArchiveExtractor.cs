using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ShadeNet.Core.Archives;

/// <summary>An archive on the disk: one file, or the parts of one (name.part1.rar, name.part2.rar …).</summary>
/// <param name="First">The part the archive is opened from.</param>
/// <param name="Parts">Every part there is now (the first included), in order.</param>
/// <param name="Stem">Its name without the archive's endings ("Holiday photos" for "Holiday photos.part1.rar").</param>
/// <param name="Split">The parts are one file cut in pieces (.7z.001, .002 …): they are read one after the other.</param>
public sealed record ArchiveSet(string First, IReadOnlyList<string> Parts, string Stem, bool Split)
{
    /// <summary>Whether a path is one of this set's parts (also a part not on the disk yet: name.part3.rar while 1 and 2 are).</summary>
    public bool Includes(string path) => Parts.Contains(path, StringComparer.OrdinalIgnoreCase) || ArchiveExtractor.SetOf(path)?.First is { } f
        && string.Equals(f, First, StringComparison.OrdinalIgnoreCase);
}

public enum ExtractStatus
{
    Extracted,

    /// <summary>Its files are locked with a password, and none (or the wrong one) was given.</summary>
    NeedsPassword,
    WrongPassword,

    /// <summary>A part of it is missing (not downloaded yet, or not at all).</summary>
    Incomplete,
    Failed,
}

/// <param name="Folder">Where its files are now (for <see cref="ExtractStatus.Extracted"/>).</param>
public sealed record ExtractResult(ExtractStatus Status, string? Folder = null, int Files = 0, string? Message = null);

/// <summary>
/// Unpacks a downloaded archive (zip, 7z, rar, tar and its compressed kinds, gz; one file or several parts) into a folder next to it:
/// a folder named after it, or (when everything in it is in one folder of its own) that folder. Nothing in it can land outside that
/// place, however its paths are written. 7-Zip does the unpacking (see <see cref="SevenZip"/>): it reads every kind of RAR (with a
/// password, in several parts), checks every file against the archive's own checksums, and asks nothing.
/// </summary>
public static partial class ArchiveExtractor
{
    [GeneratedRegex(@"^(?<stem>.+)\.part(?<n>\d{1,4})\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarParts();

    [GeneratedRegex(@"^(?<stem>.+)\.r(?<n>\d{2,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex OldRarParts();

    [GeneratedRegex(@"^(?<stem>.+)\.(?<ext>7z|zip|rar|tar)\.(?<n>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitParts();

    [GeneratedRegex(@"^(?<stem>.+)\.z(?<n>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipParts();

    static readonly string[] Single = { ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz", ".tar", ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz" };

    /// <summary>Whether a file is an archive (or a part of one), by its name.</summary>
    public static bool IsArchive(string path) => SetOf(path) != null;

    /// <summary>The archive a file is (a part of), with the parts that are on the disk now; null when it is no archive.</summary>
    public static ArchiveSet? SetOf(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);

        if (RarParts().Match(name) is { Success: true } p)
        {
            var stem = p.Groups["stem"].Value;
            var parts = Siblings(dir, $"{stem}.part*.rar", n => RarParts().Match(n) is { Success: true } m && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase)
                ? int.Parse(m.Groups["n"].Value) : (int?)null);
            var width = p.Groups["n"].Value.Length;
            var first = Path.Combine(dir, $"{stem}.part{1.ToString().PadLeft(width, '0')}.rar");
            return new ArchiveSet(first, parts, stem, false);
        }

        if (SplitParts().Match(name) is { Success: true } s)
        {
            var stem = s.Groups["stem"].Value;
            var ext = s.Groups["ext"].Value;
            var parts = Siblings(dir, $"{stem}.{ext}.*", n => SplitParts().Match(n) is { Success: true } m && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase)
                && m.Groups["ext"].Value.Equals(ext, StringComparison.OrdinalIgnoreCase) ? int.Parse(m.Groups["n"].Value) : (int?)null);
            return new ArchiveSet(Path.Combine(dir, $"{stem}.{ext}.001"), parts, stem, true);
        }

        if (OldRarParts().Match(name) is { Success: true } o)
            return OldRar(dir, o.Groups["stem"].Value);
        if (ZipParts().Match(name) is { Success: true } z)
            return SpannedZip(dir, z.Groups["stem"].Value);

        var single = Single.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase) && name.Length > e.Length);
        if (single == null) return null;
        var plain = name[..^single.Length];
        if (single == ".rar") return OldRar(dir, plain);
        if (single == ".zip") return SpannedZip(dir, plain);
        return new ArchiveSet(path, new[] { path }, plain, false);
    }

    /// <summary>name.rar, then name.r00, name.r01 … (the older way of cutting a rar archive).</summary>
    static ArchiveSet OldRar(string dir, string stem)
    {
        var first = Path.Combine(dir, stem + ".rar");
        var parts = new List<string>();
        if (File.Exists(first)) parts.Add(first);
        parts.AddRange(Siblings(dir, $"{stem}.r*", n => OldRarParts().Match(n) is { Success: true } m && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase)
            ? int.Parse(m.Groups["n"].Value) : (int?)null));
        return new ArchiveSet(first, parts, stem, false);
    }

    /// <summary>name.z01, name.z02 … then name.zip (a zip archive spread over several files).</summary>
    static ArchiveSet SpannedZip(string dir, string stem)
    {
        var last = Path.Combine(dir, stem + ".zip");
        var parts = Siblings(dir, $"{stem}.z*", n => ZipParts().Match(n) is { Success: true } m && m.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase)
            ? int.Parse(m.Groups["n"].Value) : (int?)null).ToList();
        if (File.Exists(last)) parts.Add(last);
        return new ArchiveSet(parts.Count > 1 ? parts[0] : last, parts, stem, false);
    }

    static List<string> Siblings(string dir, string pattern, Func<string, int?> number)
    {
        try
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.EnumerateFiles(dir, pattern)
                .Select(f => (Path: f, N: number(Path.GetFileName(f))))
                .Where(x => x.N != null)
                .OrderBy(x => x.N)
                .Select(x => x.Path)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Unpacks the archive (see the class) with 7-Zip. Its files are unpacked into a new hidden folder next to the archive and moved to
    /// their place once all of them are out and checked: what is on the disk already is never touched, and a failure leaves nothing
    /// half-made behind.
    /// </summary>
    /// <param name="sevenZip">7z.exe; null finds it (see <see cref="SevenZip.Locate"/>).</param>
    public static ExtractResult Extract(ArchiveSet set, string? password = null, CancellationToken ct = default, string? sevenZip = null)
    {
        if (set.Parts.Count == 0 || !File.Exists(set.First)) return new ExtractResult(ExtractStatus.Incomplete, Message: "The archive's first part is not there.");
        if (HasGap(set)) return new ExtractResult(ExtractStatus.Incomplete, Message: "A part of the archive is missing.");
        var exe = sevenZip ?? SevenZip.Locate(null);
        if (exe == null || !File.Exists(exe))
            return new ExtractResult(ExtractStatus.Failed, Message: "7-Zip was not found. Run tools\\get-tools.ps1, or choose its location in Settings > Downloads > Helper programs.");

        var dir = Path.GetDirectoryName(set.First)!;
        var work = Path.Combine(dir, ".shadenet-extract-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            File.SetAttributes(work, File.GetAttributes(work) | FileAttributes.Hidden);
            var archive = set.First;

            // a tar inside gzip, bzip2 or xz: the tar first, then what is in it
            if (IsCompressedTar(archive))
            {
                var inner = Path.Combine(work, "tar");
                var first = Run(exe, new[] { "x", "-y", "-sccUTF-8", "-bso0", "-bsp0", NoPassword(), "-o" + inner, "--", archive }, ct);
                if (first.Code != 0) return Problem(first, password);
                archive = Directory.GetFiles(inner).SingleOrDefault()
                          ?? throw new IOException("The compressed file held more than a tar.");
            }

            // what is in it, before anything is written
            var listing = Run(exe, new[] { "l", "-slt", "-sccUTF-8", PasswordArgument(password), "--", archive }, ct);
            if (listing.Code != 0) return Problem(listing, password);
            var entries = Entries(listing.Out).ToList();
            if (password == null && entries.Any(e => e.Encrypted)) return new ExtractResult(ExtractStatus.NeedsPassword);
            var files = entries.Where(e => !e.Folder).ToList();
            var unsafePath = files.FirstOrDefault(e => SafePath(work, e.Path) == null);
            if (unsafePath != null) return new ExtractResult(ExtractStatus.Failed, Message: $"The archive names a file outside its folder (\"{unsafePath.Path}\").");

            var output = Path.Combine(work, "out");
            var extract = Run(exe, new[] { "x", "-y", "-aos", "-sccUTF-8", "-bso0", "-bsp0", PasswordArgument(password), "-o" + output, "--", archive }, ct);
            if (extract.Code != 0) return Problem(extract, password);

            // nothing that leads elsewhere (a link 7-Zip made from the archive) is kept
            if (Directory.Exists(output)
                && new DirectoryInfo(output).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(i => i.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                return new ExtractResult(ExtractStatus.Failed, Message: "The archive holds links to other places on the disk, which are not unpacked.");

            Directory.CreateDirectory(output);
            var target = Destination(dir, set.Stem, files.Select(e => e.Path).ToList());
            if (target.Root == dir)
            {
                // everything is in one folder of its own: that folder is the new one
                var top = Path.GetFileName(target.Folder);
                Directory.Move(Path.Combine(output, top), target.Folder);
            }
            else
            {
                Directory.Move(output, target.Folder);
            }

            return new ExtractResult(ExtractStatus.Extracted, target.Folder, files.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ExtractResult(ExtractStatus.Failed, Message: ex.Message);
        }
        finally
        {
            TryDelete(work);
        }
    }

    static bool IsCompressedTar(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.EndsWith(".tar.gz") || name.EndsWith(".tgz") || name.EndsWith(".tar.bz2") || name.EndsWith(".tbz2")
               || name.EndsWith(".tar.xz") || name.EndsWith(".txz");
    }

    /// <summary>
    /// The password for 7-Zip. Without one, a made-up one: 7-Zip would otherwise ask for it on its input and wait; an archive that has
    /// one then says "wrong password", which is how it is known to need one.
    /// </summary>
    static string PasswordArgument(string? password) => string.IsNullOrEmpty(password) ? NoPassword() : "-p" + password;

    static string NoPassword() => "-pshadenet-no-password-" + Guid.NewGuid().ToString("N");

    /// <summary>What a failed run of 7-Zip means, from what it said.</summary>
    static ExtractResult Problem((int Code, string Out, string Err) run, string? password)
    {
        var said = run.Err + "\n" + run.Out;
        if (said.Contains("Wrong password", StringComparison.OrdinalIgnoreCase))
            return new ExtractResult(password == null ? ExtractStatus.NeedsPassword : ExtractStatus.WrongPassword);
        if (said.Contains("Missing volume", StringComparison.OrdinalIgnoreCase) || said.Contains("Unexpected end of archive", StringComparison.OrdinalIgnoreCase))
            return new ExtractResult(ExtractStatus.Incomplete, Message: "A part of the archive is missing or cut short.");
        if (said.Contains("CRC Failed", StringComparison.OrdinalIgnoreCase) || said.Contains("Data Error", StringComparison.OrdinalIgnoreCase)
            || said.Contains("Headers Error", StringComparison.OrdinalIgnoreCase))
            return new ExtractResult(ExtractStatus.Failed, Message: "The archive is damaged: " + FirstError(said));
        if (said.Contains("Can not open the file as archive", StringComparison.OrdinalIgnoreCase) || said.Contains("Cannot open the file as archive", StringComparison.OrdinalIgnoreCase))
            return new ExtractResult(ExtractStatus.Failed, Message: "It cannot be opened as an archive (it may be damaged, or not an archive).");
        return new ExtractResult(ExtractStatus.Failed, Message: FirstError(said) is { Length: > 0 } e ? e : $"7-Zip stopped with code {run.Code}.");
    }

    static string FirstError(string said)
    {
        var lines = said.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var at = lines.FindIndex(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
        if (at < 0) return lines.LastOrDefault() ?? "";
        // "ERROR: what went wrong : name", or "ERROR: path" with what went wrong on the next line
        var detail = lines[at][(lines[at].IndexOf(':') + 1)..].Trim();
        var namesAFile = detail.Contains('\\') || detail.Contains('/');
        return namesAFile && at + 1 < lines.Count && !lines[at + 1].StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
            ? $"{lines[at + 1]} ({Path.GetFileName(detail)})"
            : detail;
    }

    /// <summary>One entry of 7-Zip's technical listing (<c>l -slt</c>).</summary>
    sealed record Entry(string Path, bool Folder, bool Encrypted);

    /// <summary>The entries of a technical listing: blocks of "Name = value" lines after its "----------" line.</summary>
    static IEnumerable<Entry> Entries(string listing)
    {
        var lines = listing.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == "----------");
        if (start < 0) yield break;
        string? path = null;
        bool folder = false, encrypted = false;
        for (var i = start + 1; i <= lines.Length; i++)
        {
            var line = i < lines.Length ? lines[i] : "";
            if (line.Length == 0)
            {
                if (path != null) yield return new Entry(path, folder, encrypted);
                path = null;
                folder = encrypted = false;
                continue;
            }

            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            var name = eq < 0 ? line.TrimEnd(' ', '=') : line[..eq];
            var value = eq < 0 ? "" : line[(eq + 3)..];
            switch (name)
            {
                case "Path":
                    path = value;
                    break;
                case "Folder":
                    folder |= value == "+";
                    break;
                case "Attributes":
                    folder |= value.StartsWith('D');
                    break;
                case "Encrypted":
                    encrypted |= value == "+";
                    break;
            }
        }
    }

    /// <summary>Runs 7-Zip (it answers in UTF-8: names in any language come through); stopped when <paramref name="ct"/> is.</summary>
    static (int Code, string Out, string Err) Run(string exe, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("7-Zip did not start.");
        process.StandardInput.Close();   // (it never waits for an answer)
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using (ct.Register(() =>
               {
                   try
                   {
                       process.Kill(entireProcessTree: true);
                   }
                   catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                   {
                       // it ended by itself
                   }
               }))
        {
            process.WaitForExit();
        }

        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Where the archive's files go: a new folder named after it; or, when every file in it is in one folder of its own, the archive's
    /// own folder (that folder is then the new one: it is not put inside another of the same name).
    /// </summary>
    static (string Root, string Folder) Destination(string dir, string stem, IReadOnlyList<string> keys)
    {
        if (keys.Count > 0)
        {
            var tops = keys.Select(k => k.Replace('\\', '/').TrimStart('/').Split('/')).ToList();
            if (tops.All(t => t.Length > 1) && tops.Select(t => t[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                var top = tops[0][0];
                var existing = Path.Combine(dir, top);
                if (!Directory.Exists(existing) && !File.Exists(existing) && SafePath(dir, top + "/x") != null) return (dir, existing);
            }
        }

        var folder = Unique(Path.Combine(dir, stem));
        return (folder, folder);
    }

    static string Unique(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        for (var i = 1; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Where an entry goes under <paramref name="root"/>; null when its path would lead outside it ("../", a drive, a rooted path).</summary>
    internal static string? SafePath(string root, string key)
    {
        var relative = key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    [GeneratedRegex(@"(?:\.part|\.r|\.z|\.)(?<n>\d{1,4})(?:\.rar)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PartNumber();

    /// <summary>A part in the middle is not there (part 1 and 3, no 2): the archive is not whole yet, whatever the parts that are say.</summary>
    static bool HasGap(ArchiveSet set)
    {
        var numbers = set.Parts.Select(p => PartNumber().Match(Path.GetFileName(p)))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups["n"].Value))
            .OrderBy(n => n)
            .ToList();
        for (var i = 1; i < numbers.Count; i++)
            if (numbers[i] != numbers[i - 1] + 1) return true;
        return false;
    }

    static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for the user (a hidden folder next to the archive)
        }
    }
}
