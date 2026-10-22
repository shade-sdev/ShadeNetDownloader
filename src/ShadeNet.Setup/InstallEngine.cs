using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;

namespace ShadeNet.Setup;

/// <summary>Something the user can act on (close the program, pick another folder); carries the exit code of a silent run.</summary>
public sealed class SetupException : Exception
{
    public SetupException(string message, int exitCode = 1, Exception? inner = null) : base(message, inner) => ExitCode = exitCode;

    public int ExitCode { get; }
}

/// <param name="RegistryRoot">Only when it is not the real one (tests and dry runs): an update must write to the same place.</param>
/// <param name="DataDir">Only when it is not the usual one.</param>
public sealed record InstallManifest(string Version, List<string> Files, string? StartMenuShortcut, string? DesktopShortcut, string? RegistryRoot = null, string? DataDir = null);

/// <summary>
/// Installs and removes ShadeNet for the current user: no administrator rights, nothing machine-wide. It unpacks the program
/// (a zip), writes a manifest of what it put where, makes shortcuts, registers the entry in "Apps &amp; features" and (as the
/// user chose) start-up and the browser integration. An upgrade replaces the files, removes the ones the old version had
/// and the new one does not, and never touches the user's data.
/// </summary>
public sealed class InstallEngine
{
    public const string AppExe = "ShadeNetDownloader.exe";
    public const string UninstallExe = "Uninstall.exe";
    public const string ManifestName = "install.json";
    public const string RunValueName = "ShadeNetDownloader";
    public const string UninstallKeyName = "ShadeNetDownloader";

    readonly InstallOptions _o;
    readonly Action<string> _log;

    public InstallEngine(InstallOptions options, Action<string>? log = null)
    {
        _o = options;
        _log = log ?? (_ => { });
    }

    string ManifestPath => Path.Combine(_o.InstallDir, ManifestName);
    string UninstallKeyPath => $@"{_o.RegistryRoot}\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}";
    string RunKeyPath => $@"{_o.RegistryRoot}\Microsoft\Windows\CurrentVersion\Run";
    string StartMenuShortcutPath => Path.Combine(_o.StartMenuDir, InstallOptions.ProductName + ".lnk");
    string DesktopShortcutPath => Path.Combine(_o.DesktopDir, InstallOptions.ProductName + ".lnk");

    /// <summary>The version already installed in the target folder, or <c>null</c>.</summary>
    public string? InstalledVersion => ReadManifest()?.Version;

    public InstallManifest? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(ManifestPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    // ================================================================================================================
    // install
    // ================================================================================================================

    public void Install(Stream payload, string version, Action<double>? progress = null, CancellationToken ct = default)
    {
        var previous = ReadManifest();

        // A folder that is not ours and not empty is not touched: it may be somebody's files.
        if (Directory.Exists(_o.InstallDir) && previous == null && Directory.EnumerateFileSystemEntries(_o.InstallDir).Any())
            throw new SetupException($"The folder \"{_o.InstallDir}\" is not empty and does not hold a ShadeNet installation. Choose another folder.", 2);

        EnsureNotRunning();

        Directory.CreateDirectory(_o.InstallDir);
        var files = Extract(payload, progress, ct);

        // what the old version had and this one does not: gone (so an upgrade leaves nothing stale behind)
        if (previous != null)
            foreach (var stale in previous.Files.Except(files, StringComparer.OrdinalIgnoreCase))
                TryDelete(Path.Combine(_o.InstallDir, stale));

        var startMenu = _o.StartMenuShortcut ? CreateShortcut(StartMenuShortcutPath) : null;
        var desktop = _o.DesktopShortcut ? CreateShortcut(DesktopShortcutPath) : null;
        if (!_o.StartMenuShortcut) TryDelete(StartMenuShortcutPath);
        if (!_o.DesktopShortcut) TryDelete(DesktopShortcutPath);

        var manifest = new InstallManifest(version, files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(), startMenu, desktop,
            _o.RegistryRoot != "Software" ? _o.RegistryRoot : null, _o.DataDir != new InstallOptions().DataDir ? _o.DataDir : null);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        RegisterUninstallEntry(version, files);
        SetStartup(_o.StartWithWindows);
        if (_o.BrowserIntegration) RunApp("--install-browser-integration");
        _log($"installed {version} into {_o.InstallDir}");
    }

    List<string> Extract(Stream payload, Action<double>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(_o.InstallDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();

        using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        var total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(_o.InstallDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("The installer package is damaged (a file points outside the folder).", 4);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".setup-new";
            try
            {
                entry.ExtractToFile(temp, overwrite: true);
                Replace(temp, target);
            }
            catch (IOException ex)
            {
                TryDelete(temp);
                throw new SetupException($"Could not write \"{entry.FullName}\": {ex.Message}", 5, ex);
            }

            files.Add(entry.FullName.Replace('/', '\\'));
            done += entry.Length;
            progress?.Invoke(done / (double)total);
        }

        return files;
    }

    /// <summary>A file the previous version had open (a browser holding the host) is retried for a moment before giving up.</summary>
    static void Replace(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                Thread.Sleep(250);
            }
        }
    }

    // ================================================================================================================
    // update
    // ================================================================================================================

    /// <summary>Where an update works: the new files are unpacked here first, and the files they replace wait here until it is certain.</summary>
    public const string UpdateFolder = ".update";

    static bool IsTools(string relative) => relative.StartsWith(@"tools\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the installed program with the one in an update package. The package is unpacked and checked completely before
    /// anything is touched; every replaced file is kept until the last one is in place, and a failure puts them all back, so an
    /// update either completes or leaves the program as it was. tools\ (yt-dlp, ffmpeg: not in the package, they have their own
    /// updates), shortcuts, start-up, the browser connection and the user's data are not touched. Returns the version now installed.
    /// </summary>
    public string Update(Stream package, string? versionHint = null, Action<double>? progress = null, CancellationToken ct = default)
    {
        var previous = ReadManifest() ?? throw new SetupException($"ShadeNet is not installed in \"{_o.InstallDir}\".", 6);
        AdoptInstallSettings(_o, previous);

        var staging = Path.Combine(_o.InstallDir, UpdateFolder);
        RecoverInterruptedUpdate(staging);
        EnsureNotRunning();

        var fresh = Path.Combine(staging, "new");
        var old = Path.Combine(staging, "old");
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(old);
        try
        {
            var files = ExtractToStaging(package, fresh, progress, ct);
            if (!files.Contains(AppExe, StringComparer.OrdinalIgnoreCase))
                throw new SetupException("The update package is damaged (the program is not in it).", 4);

            var carried = previous.Files.Where(IsTools).Except(files, StringComparer.OrdinalIgnoreCase).ToList();
            var stale = previous.Files.Except(files, StringComparer.OrdinalIgnoreCase).Where(f => !IsTools(f)).ToList();
            Swap(files, stale, fresh, old);

            var version = InstalledProgramVersion() ?? versionHint ?? previous.Version;
            var all = files.Concat(carried).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var manifest = new InstallManifest(version, all, previous.StartMenuShortcut, previous.DesktopShortcut,
                _o.RegistryRoot != "Software" ? _o.RegistryRoot : null, _o.DataDir != new InstallOptions().DataDir ? _o.DataDir : null);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(staging, "committed"), version);   // from here an interruption must not undo anything

            RegisterUninstallEntry(version, all);
            _log($"updated {previous.Version} -> {version} in {_o.InstallDir}");
            return version;
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    /// <summary>The update runs in a copy of the uninstaller that knows nothing of how the program was installed: it reads it from the manifest.</summary>
    internal static void AdoptInstallSettings(InstallOptions options, InstallManifest manifest)
    {
        if (manifest.RegistryRoot != null && options.RegistryRoot == "Software") options.RegistryRoot = manifest.RegistryRoot;
        if (manifest.DataDir != null && options.DataDir == new InstallOptions().DataDir) options.DataDir = manifest.DataDir;
    }

    string? InstalledProgramVersion()
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(_o.AppExe).ProductVersion?.Split('+')[0];
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Unpacks the whole package into a folder of its own, checking every name; nothing of the installation is touched.</summary>
    List<string> ExtractToStaging(Stream package, string destination, Action<double>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new SetupException("The update package is damaged (it is not a valid zip file).", 4, ex);
        }

        using (zip)
        {
            var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
            var total = Math.Max(1, entries.Sum(e => e.Length));
            long done = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var relative = entry.FullName.Replace('/', '\\');
                var target = Path.GetFullPath(Path.Combine(destination, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith(UpdateFolder, StringComparison.OrdinalIgnoreCase)
                    || relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException("The update package is damaged (a file is where it must not be).", 4);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    throw new SetupException($"The update package is damaged or cannot be unpacked (\"{entry.FullName}\": {ex.Message}).", 4, ex);
                }

                files.Add(relative);
                done += entry.Length;
                progress?.Invoke(done / (double)total);
            }
        }

        return files;
    }

    /// <summary>Puts the new files in place, keeping each replaced file; any failure puts every one back.</summary>
    void Swap(List<string> files, List<string> stale, string fresh, string old)
    {
        var steps = new List<(string Relative, bool HadOld, bool Placed)>();
        try
        {
            foreach (var relative in files)
            {
                var target = Path.Combine(_o.InstallDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var hadOld = File.Exists(target);
                if (hadOld) MoveFile(target, Path.Combine(old, relative));
                steps.Add((relative, hadOld, false));
                MoveFile(Path.Combine(fresh, relative), target);
                steps[^1] = (relative, hadOld, true);
            }

            foreach (var relative in stale)
            {
                var target = Path.Combine(_o.InstallDir, relative);
                if (!File.Exists(target)) continue;
                MoveFile(target, Path.Combine(old, relative));
                steps.Add((relative, true, false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (relative, hadOld, placed) in Enumerable.Reverse(steps))
            {
                var target = Path.Combine(_o.InstallDir, relative);
                try
                {
                    if (placed) File.Delete(target);
                    if (hadOld) File.Move(Path.Combine(old, relative), target, overwrite: true);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    _log($"could not put {relative} back: {undo.Message}");
                }
            }

            throw new SetupException($"The update could not be applied ({ex.Message}). The previous version was put back.", 5, ex);
        }
    }

    static void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Replace(from, to);   // retries for a moment: a virus scanner or a browser may hold the file
    }

    /// <summary>
    /// An earlier update that was cut off (power, a killed process) left its backup: when it had not reached the point of no return
    /// the old files are put back, so the program is whole again before this update starts.
    /// </summary>
    void RecoverInterruptedUpdate(string staging)
    {
        if (!Directory.Exists(staging)) return;
        var old = Path.Combine(staging, "old");
        if (!File.Exists(Path.Combine(staging, "committed")) && Directory.Exists(old))
        {
            foreach (var file in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                try
                {
                    MoveFile(file, Path.Combine(_o.InstallDir, Path.GetRelativePath(old, file)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log($"could not undo an interrupted update ({file}): {ex.Message}");
                }
            }

            _log("an interrupted update was undone");
        }

        TryDeleteFolder(staging);
    }

    static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover folder is better than a failed update
        }
    }

    // ================================================================================================================
    // uninstall
    // ================================================================================================================

    public void Uninstall()
    {
        var manifest = ReadManifest();
        if (manifest == null && !File.Exists(Path.Combine(_o.InstallDir, AppExe)))
            throw new SetupException($"ShadeNet is not installed in \"{_o.InstallDir}\".", 6);

        EnsureNotRunning();
        if (_o.BrowserIntegration) RunApp("--uninstall-browser-integration");

        DeleteRunValue();
        TryDelete(manifest?.StartMenuShortcut ?? StartMenuShortcutPath);
        TryDelete(manifest?.DesktopShortcut ?? DesktopShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        if (manifest != null)
            foreach (var file in manifest.Files) TryDelete(Path.Combine(_o.InstallDir, file));
        TryDelete(ManifestPath);
        RemoveEmptyFolders(_o.InstallDir);

        if (_o.RemoveData && Directory.Exists(_o.DataDir))
        {
            try
            {
                Directory.Delete(_o.DataDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"could not remove the data folder: {ex.Message}");
            }
        }

        _log("uninstalled");
    }

    // ================================================================================================================
    // the parts
    // ================================================================================================================

    /// <summary>Asks a running ShadeNet from this folder to quit (really: closing its window only hides it in the tray), then waits.</summary>
    void EnsureNotRunning()
    {
        var running = RunningFromInstallDir();
        if (running.Count == 0) return;

        _log($"asking {running.Count} running process(es) to quit");
        RunApp("--quit");
        foreach (var p in running.Where(p => p.ProcessName.Equals("ShadeNetDownloader", StringComparison.OrdinalIgnoreCase)))
            p.WaitForExit(_o.QuitWaitMs);

        // the browser's helper (it starts again when the browser needs it) is not worth a question
        foreach (var p in RunningFromInstallDir().Where(p => p.ProcessName.StartsWith("ShadeNet.NativeHost", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        }

        if (RunningFromInstallDir().Any(p => p.ProcessName.Equals("ShadeNetDownloader", StringComparison.OrdinalIgnoreCase)))
            throw new SetupException("ShadeNet Downloader is still running. Close it (also from the tray icon: right-click, Exit) and try again.", 3);
    }

    List<Process> RunningFromInstallDir()
    {
        var root = Path.GetFullPath(_o.InstallDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var found = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.StartsWith("ShadeNet", StringComparison.OrdinalIgnoreCase) && p.MainModule?.FileName is { } file
                    && file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !file.EndsWith(UninstallExe, StringComparison.OrdinalIgnoreCase))
                    found.Add(p);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // another user's process, or one that just ended
            }
        }

        return found;
    }

    /// <summary>Runs the installed program with one switch and waits (a few seconds at most); problems are logged, never fatal.</summary>
    void RunApp(string argument)
    {
        if (!File.Exists(_o.AppExe)) return;
        try
        {
            var info = new ProcessStartInfo(_o.AppExe, argument) { UseShellExecute = false, CreateNoWindow = true };
            // the app must look at the data folder the user's installation uses (tests point both at a throw-away folder)
            info.Environment["SHADENET_DATA_DIR"] = _o.DataDir;
            using var process = Process.Start(info);
            if (process != null && !process.WaitForExit(20000)) _log($"{argument} took too long");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log($"{argument} failed: {ex.Message}");
        }
    }

    string CreateShortcut(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Shortcuts.Create(path, _o.AppExe, _o.InstallDir, InstallOptions.ProductName);
        return path;
    }

    void RegisterUninstallEntry(string version, List<string> files)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        var uninstaller = Path.Combine(_o.InstallDir, UninstallExe);
        var overrides = new List<string>();
        if (_o.RegistryRoot != "Software") overrides.AddRange(new[] { "--registry-root", Quote(_o.RegistryRoot) });
        if (_o.DataDir != new InstallOptions().DataDir) overrides.AddRange(new[] { "--data-dir", Quote(_o.DataDir) });

        key.SetValue("DisplayName", InstallOptions.ProductName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "ShadeNet");
        key.SetValue("InstallLocation", _o.InstallDir);
        key.SetValue("DisplayIcon", _o.AppExe);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall --dir \"{_o.InstallDir}\" {string.Join(' ', overrides)}".TrimEnd());
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --silent --dir \"{_o.InstallDir}\" {string.Join(' ', overrides)}".TrimEnd());
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, SizeOf(files) / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    long SizeOf(IEnumerable<string> files)
    {
        long total = 0;
        foreach (var f in files)
        {
            try
            {
                total += new FileInfo(Path.Combine(_o.InstallDir, f)).Length;
            }
            catch (IOException)
            {
                // not counted
            }
        }

        return total;
    }

    static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>The same entry the app's own "Start with Windows" switch writes: <c>"exe" --minimized</c>.</summary>
    void SetStartup(bool on)
    {
        if (on)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RunValueName, $"\"{_o.AppExe}\" --minimized", RegistryValueKind.String);
        }
        else if (ReadManifest() == null || !_o.StartWithWindows)
        {
            // an upgrade keeps the user's choice: only a fresh install with the box unticked has nothing to remove
        }
    }

    void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        // only our own entry, and only when it points into this installation
        if (key?.GetValue(RunValueName) is string value && value.Contains(_o.InstallDir, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use: a leftover file is better than a failed uninstall
        }
    }

    static void RemoveEmptyFolders(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException)
            {
                // not empty after all
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (IOException)
        {
            // something of the user's is in there: it stays
        }
    }
}
