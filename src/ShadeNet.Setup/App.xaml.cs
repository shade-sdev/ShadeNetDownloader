using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace ShadeNet.Setup;

public partial class App : Application
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ShadeNetSetup.log");

    /// <summary>"1.0.0": the version this setup installs (the one of the program inside it).</summary>
    public static string VersionText { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // a log that cannot be written is not worth a failed install
        }
    }

    /// <summary>The program to install: the zip inside this setup, or the one named with <c>--payload</c>.</summary>
    public static Stream OpenPayload(InstallOptions options)
    {
        if (options.PayloadPath != null) return File.OpenRead(options.PayloadPath);
        return typeof(App).Assembly.GetManifestResourceStream("payload.zip")
               ?? throw new SetupException("This setup program does not contain ShadeNet Downloader (it is the uninstaller).", 8);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = InstallOptions.Parse(e.Args);
        DispatcherUnhandledException += (_, args) =>
        {
            Log("unhandled: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        switch (options.Mode)
        {
            case SetupMode.Install when options.Silent:
                Quit(RunSilent(() =>
                {
                    using var payload = OpenPayload(options);
                    new InstallEngine(options, Log).Install(payload, VersionText);
                    if (options.LaunchAfter) StartApp(options);
                }));
                break;

            case SetupMode.Install:
                new SetupWindow(options).Show();
                break;

            case SetupMode.Uninstall when options.Silent:
                StartStage2(options);
                Quit(0);
                break;

            case SetupMode.Uninstall:
                if (MessageBox.Show("Remove ShadeNet Downloader from this computer?\n\nYour downloaded files are not touched.", "Uninstall ShadeNet Downloader",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    Quit(0);
                    break;
                }

                options.RemoveData = MessageBox.Show("Also delete the download list and the settings?\n\nChoose No to keep them in case you install ShadeNet again.",
                    "Uninstall ShadeNet Downloader", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
                StartStage2(options);
                Quit(0);
                break;

            case SetupMode.UninstallStage2:
                Quit(Stage2(options));
                break;

            case SetupMode.Update when options.Silent:
                Quit(RunSilent(() => ApplyUpdate(options)));
                break;

            case SetupMode.Update:
                new SetupWindow(options).Show();
                break;
        }
    }

    void Quit(int code) => Shutdown(code);

    static int RunSilent(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (SetupException ex)
        {
            Log(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Log("failed: " + ex);
            return 1;
        }
    }

    /// <summary>Waits (a little) for the program that is being replaced to end: it is the one that started the update, then quit.</summary>
    public static void WaitForProcess(int processId)
    {
        if (processId <= 0) return;
        try
        {
            Process.GetProcessById(processId).WaitForExit(30000);
        }
        catch (ArgumentException)
        {
            // already gone
        }
    }

    /// <summary>The update without a window: wait for the program to end, replace its files, start it again when asked.</summary>
    static void ApplyUpdate(InstallOptions options)
    {
        if (string.IsNullOrEmpty(options.PackagePath) || !File.Exists(options.PackagePath))
            throw new SetupException("The update package is missing.", 4);

        WaitForProcess(options.WaitForProcessId);
        using (var package = File.OpenRead(options.PackagePath))
            new InstallEngine(options, Log).Update(package);
        if (options.LaunchAfter) StartApp(options);
    }

    /// <summary>The update runs as a copy of the uninstaller in the temp folder (the original is one of the files it replaces): that copy is removed when it is done.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        try
        {
            var me = Environment.ProcessPath;
            if (me != null && Path.GetFileName(me).StartsWith("ShadeNetUpdate-", StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(me).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{me}\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // a stray file in temp
        }
    }

    public static void StartApp(InstallOptions options)
    {
        if (File.Exists(options.AppExe))
            Process.Start(new ProcessStartInfo(options.AppExe) { UseShellExecute = false, WorkingDirectory = options.InstallDir });
    }

    // ---- uninstalling: the program that is running must not be inside the folder that is deleted ----

    /// <summary>Copies this program to the temp folder and runs the copy, which removes everything including this file.</summary>
    static void StartStage2(InstallOptions options)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ShadeNetUninstall-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.Copy(Environment.ProcessPath!, temp, overwrite: true);

        var info = new ProcessStartInfo(temp) { UseShellExecute = false };
        foreach (var a in new[] { "--uninstall-stage2", "--wait-for", Environment.ProcessId.ToString(), "--dir", options.InstallDir,
                     "--registry-root", options.RegistryRoot, "--data-dir", options.DataDir,
                     "--start-menu-dir", options.StartMenuDir, "--desktop-dir", options.DesktopDir })
            info.ArgumentList.Add(a);
        if (options.RemoveData) info.ArgumentList.Add("--remove-data");
        if (options.Silent) info.ArgumentList.Add("--silent");
        if (!options.BrowserIntegration) info.ArgumentList.Add("--no-browser-integration");
        Process.Start(info);
    }

    static int Stage2(InstallOptions options)
    {
        if (options.WaitForProcessId > 0)
        {
            try
            {
                Process.GetProcessById(options.WaitForProcessId).WaitForExit(15000);
            }
            catch (ArgumentException)
            {
                // the first stage is already gone
            }
        }

        var code = RunSilent(() => new InstallEngine(options, Log).Uninstall());
        if (!options.Silent)
        {
            MessageBox.Show(code == 0 ? "ShadeNet Downloader was removed." : "ShadeNet Downloader could not be removed completely. See " + LogPath,
                "Uninstall ShadeNet Downloader", MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // this copy lives in the temp folder: ask the shell to delete it when it is gone
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{Environment.ProcessPath}\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // a stray file in temp
        }

        return code;
    }
}
