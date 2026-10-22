using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShadeNet.Setup.Tests;

/// <summary>
/// A running "ShadeNetDownloader.exe" inside an installed folder, as the installer meets it when it is asked to replace the program
/// (see tests/ShadeNet.StandIn). A real .NET program of our own, not a copy of something from Windows: it behaves the same on
/// every machine and image. It quits when the installer's "--quit" reaches it, or, when it does not obey, lives on.
/// </summary>
sealed class StandInProgram : IDisposable
{
    static readonly string[] Names = { "ShadeNetDownloader.exe", "ShadeNetDownloader.dll", "ShadeNetDownloader.runtimeconfig.json", "ShadeNetDownloader.deps.json" };
    static readonly string[] Variables = { "SHADENET_STANDIN_EVENT", "SHADENET_STANDIN_OBEY", "DOTNET_ROOT" };

    readonly Dictionary<string, string?> _before = new();
    readonly string _event = "ShadeNetStandIn." + Guid.NewGuid().ToString("N");

    public Process Process { get; }

    public StandInProgram(string installDir, bool obeysQuit)
    {
        var source = Folder();
        foreach (var name in Names) File.Copy(Path.Combine(source, name), Path.Combine(installDir, name), overwrite: true);

        // The installer starts "ShadeNetDownloader.exe --quit" itself, from its own environment: this process's. So the stand-in is
        // told here which event to use and whether to obey; and where the .NET runtime is (a machine may not have it registered).
        foreach (var v in Variables) _before[v] = Environment.GetEnvironmentVariable(v);
        Environment.SetEnvironmentVariable("SHADENET_STANDIN_EVENT", _event);
        Environment.SetEnvironmentVariable("SHADENET_STANDIN_OBEY", obeysQuit ? "1" : "0");
        Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")));

        Process = Process.Start(new ProcessStartInfo(Path.Combine(installDir, "ShadeNetDownloader.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installDir,
        })!;

        // The test goes on only when the stand-in is really running: a program that failed to start would make the tests below mean nothing.
        for (var i = 0; i < 100; i++)
        {
            if (Process.HasExited) throw new InvalidOperationException($"The stand-in program ended at once (exit code {Process.ExitCode}).");
            if (EventWaitHandle.TryOpenExisting(@"Local\" + _event, out var handle))
            {
                handle.Dispose();
                return;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The stand-in program did not start within ten seconds.");
    }

    static string Folder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var found = new[] { "Release", "Debug" }
                .Select(cfg => Path.Combine(dir.FullName, "tests", "ShadeNet.StandIn", "bin", cfg, "net10.0"))
                .Where(d => File.Exists(Path.Combine(d, "ShadeNetDownloader.exe")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "ShadeNetDownloader.exe")))
                .FirstOrDefault();
            if (found != null) return found;
        }

        throw new FileNotFoundException("tests/ShadeNet.StandIn was not built.");
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited) Process.Kill();
            Process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // gone already
        }

        Process.Dispose();
        foreach (var (name, value) in _before) Environment.SetEnvironmentVariable(name, value);
    }
}
