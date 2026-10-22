using System.Diagnostics;

namespace ShadeNet.Bridge.Tests;

/// <summary>Runs the extension's own unit tests (node's built-in runner) so one <c>dotnet test</c> covers every layer.</summary>
public class ExtensionTests
{
    [Fact]
    public async Task The_extensions_unit_tests_pass()
    {
        string? folder = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && folder == null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "extension", "chromium");
            if (Directory.Exists(Path.Combine(candidate, "test"))) folder = candidate;
        }

        Assert.NotNull(folder);

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo("node", "--test \"test/*.test.mjs\"")
            {
                WorkingDirectory = folder!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;   // node is not installed on this machine: the extension tests cannot run here, the rest still do
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("node did not finish");
        }

        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}
