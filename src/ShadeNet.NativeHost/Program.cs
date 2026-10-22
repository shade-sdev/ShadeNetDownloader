using System.Diagnostics;
using System.Text.Json;
using ShadeNet.Bridge;

namespace ShadeNet.NativeHost;

/// <summary>
/// The native messaging host the browser starts for the extension (<c>chrome.runtime.connectNative</c>). It is a
/// transparent relay between the browser's stdio and the app's pipe, modelled on IDM's <c>IDMMsgHost.exe</c> (report §5):
/// frames are copied, not interpreted. Rules of the road: stdout belongs to the browser, so only protocol frames are
/// ever written to it; diagnostics go to a small log file.
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        var log = new HostLog();
        log.Write($"start pid={Environment.ProcessId} args=[{string.Join(" ", args)}]");

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var browserWrite = new SemaphoreSlim(1, 1);

        async Task ToBrowser(object message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BridgeProtocol.Json);
            await browserWrite.WaitAsync();
            try
            {
                await Framing.WriteAsync(stdout, bytes);
            }
            finally
            {
                browserWrite.Release();
            }
        }

        // The extension's first frame is its hello. It may say `"launch": false` ("is the app there? do not start it"): the
        // toolbar popup asks that to show the state without waking an app the user closed. A browser that sends nothing
        // for a moment is treated as wanting the app (what the host always did).
        // (A read on the console's stdin cannot be cancelled reliably, so it is raced against a timer; a frame that comes
        // later is picked up by the relay below.)
        byte[]? hello = null;
        var allowLaunch = true;
        Task<byte[]?>? firstRead = Framing.ReadAsync(stdin, BridgeProtocol.MaxFromBrowserBytes);
        if (await Task.WhenAny(firstRead, Task.Delay(TimeSpan.FromSeconds(3))) == firstRead)
        {
            hello = await firstRead;
            if (hello == null)
            {
                log.Write("browser closed before saying hello");
                return 0;
            }

            allowLaunch = WantsLaunch(hello);
            firstRead = null;
        }
        else
        {
            log.Write("no hello from the browser in time");
        }

        var pipeName = BridgeProtocol.PipeName();
        await using var app = await ConnectToAppAsync(pipeName, log, allowLaunch);
        if (app == null)
        {
            log.Write(allowLaunch ? "app unavailable" : "app not running (not asked to start it)");
            await ToBrowser(allowLaunch
                ? new HostError { Code = "app-unavailable", Message = "ShadeNet Downloader is not running and could not be started." }
                : new HostError { Code = "app-not-running", Message = "ShadeNet Downloader is not running." });
            return 2;
        }

        try
        {
            await app.SendAsync(new HostHello { Origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal)) });
            if (hello != null) await app.SendRawAsync(hello, CancellationToken.None);

            using var done = new CancellationTokenSource();

            var toApp = Task.Run(async () =>
            {
                while (true)
                {
                    var frame = firstRead != null
                        ? await firstRead.WaitAsync(done.Token)   // the read that was still waiting when the app was connected
                        : await Framing.ReadAsync(stdin, BridgeProtocol.MaxFromBrowserBytes, done.Token);
                    firstRead = null;
                    if (frame == null) return "browser closed";
                    await app.SendRawAsync(frame, done.Token);
                }
            });

            var toBrowser = Task.Run(async () =>
            {
                while (true)
                {
                    var frame = await Framing.ReadAsync(app.Stream, BridgeProtocol.MaxToBrowserBytes, done.Token);
                    if (frame == null) return "app closed";
                    await browserWrite.WaitAsync(done.Token);
                    try
                    {
                        await Framing.WriteAsync(stdout, frame, done.Token);
                    }
                    finally
                    {
                        browserWrite.Release();
                    }
                }
            });

            var first = await Task.WhenAny(toApp, toBrowser);
            string reason;
            try
            {
                reason = await first;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
            }

            log.Write("stop: " + reason);
            await done.CancelAsync();
            return 0;
        }
        catch (Exception ex)
        {
            log.Write("fatal: " + ex);
            return 1;
        }
    }

    /// <summary>Connects to the running app; if there is none, starts it (like IDM's host does) and waits for its pipe.</summary>
    static async Task<BridgeConnection?> ConnectToAppAsync(string pipeName, HostLog log, bool allowLaunch)
    {
        var connection = await BridgeClient.TryConnectAsync(pipeName, TimeSpan.FromMilliseconds(400));
        if (connection != null) return connection;

        if (!allowLaunch || Environment.GetEnvironmentVariable("SHADENET_HOST_NO_LAUNCH") == "1") return null;
        if (!TryLaunchApp(log)) return null;

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            connection = await BridgeClient.TryConnectAsync(pipeName, TimeSpan.FromMilliseconds(500));
            if (connection != null) return connection;
            await Task.Delay(250);
        }

        return null;
    }

    /// <summary>False only when the hello carries <c>"launch": false</c>; anything unreadable keeps the old behaviour (start the app).</summary>
    static bool WantsLaunch(byte[] hello)
    {
        try
        {
            using var doc = JsonDocument.Parse(hello);
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("launch", out var launch)
                     && launch.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    static bool TryLaunchApp(HostLog log)
    {
        try
        {
            var config = HostConfig.Load();
            if (config?.AppPath == null || !File.Exists(config.AppPath))
            {
                log.Write("no app path configured");
                return false;
            }

            var info = new ProcessStartInfo(config.AppPath, "--minimized")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(config.AppPath)!,
            };
            if (!string.IsNullOrEmpty(config.DotnetRoot)) info.Environment["DOTNET_ROOT"] = config.DotnetRoot;

            Process.Start(info);
            log.Write("launched " + config.AppPath);
            return true;
        }
        catch (Exception ex)
        {
            log.Write("launch failed: " + ex.Message);
            return false;
        }
    }
}

/// <summary>Where the app lives; written into the data folder by the app's "install browser integration".</summary>
internal sealed record HostConfig(string? AppPath, string? DotnetRoot)
{
    public static HostConfig? Load()
    {
        var path = Path.Combine(BridgeProtocol.DataDir(), "host.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), BridgeProtocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A tiny log (capped at 256 KB, then restarted) so "the extension cannot reach the app" can be diagnosed.</summary>
internal sealed class HostLog
{
    readonly string? _path;

    public HostLog()
    {
        try
        {
            var dir = BridgeProtocol.DataDir();
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "host.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 256 * 1024) File.Delete(_path);
        }
        catch
        {
            _path = null;
        }
    }

    public void Write(string line)
    {
        if (_path == null) return;
        try
        {
            File.AppendAllText(_path, $"[{DateTime.Now:u}] {line}{Environment.NewLine}");
        }
        catch
        {
            // logging must never disturb the relay
        }
    }
}
