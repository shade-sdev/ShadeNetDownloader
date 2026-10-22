using System.Diagnostics;

namespace ShadeNet.Setup.Tests;

/// <summary>The scripts the release workflow runs, tried against a throw-away git repository.</summary>
public sealed class ReleaseScriptTests : IDisposable
{
    readonly string _repo = Path.Combine(Path.GetTempPath(), "shadenet-rel-" + Guid.NewGuid().ToString("N")[..8]);

    public ReleaseScriptTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, true);
        }
        catch
        {
            // best effort
        }
    }

    static string? Script(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ci", name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    static (int Code, string Output) Run(string file, string arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    static bool Has(string program)
    {
        try
        {
            return Run(program, "--version").Code == 0;
        }
        catch
        {
            return false;
        }
    }

    void Git(string arguments) => Assert.True(Run("git", arguments, _repo).Code == 0, "git " + arguments);

    void NewRepo(string majorMinor, params string[] tags)
    {
        File.WriteAllText(Path.Combine(_repo, "Directory.Build.props"),
            $"<Project><PropertyGroup><VersionMajorMinor>{majorMinor}</VersionMajorMinor></PropertyGroup></Project>");
        Git("init -q");
        Git("config user.email t@t.example");
        Git("config user.name t");
        Git("add -A");
        Git("commit -q -m one");
        foreach (var tag in tags) Git($"tag {tag}");
    }

    string NextVersion(string? output = null)
    {
        var args = $"-NoProfile -File \"{Script("next-version.ps1")}\" -Root \"{_repo}\"" + (output != null ? $" -Output \"{output}\"" : "");
        var (code, text) = Run("pwsh", args);
        Assert.True(code == 0, text);
        return text.Split('\n').First(l => l.StartsWith("version=")).Trim()["version=".Length..];
    }

    [Fact]
    public void The_first_release_of_a_major_minor_is_dot_one_so_a_branch_or_tag_called_dot_zero_never_clashes()
    {
        if (Script("next-version.ps1") == null || !Has("git") || !Has("pwsh")) return;   // a machine without them cannot run the workflow either
        NewRepo("1.0");
        Git("branch v1.0.0");
        Assert.Equal("1.0.1", NextVersion());
    }

    [Fact]
    public void The_patch_is_one_more_than_the_highest_tag_of_that_major_minor_compared_as_numbers()
    {
        if (Script("next-version.ps1") == null || !Has("git") || !Has("pwsh")) return;
        NewRepo("1.0", "v1.0.1", "v1.0.2", "v1.0.10", "v1.0.9", "v1.1.5", "v2.0.7", "v1.0.0-beta", "release-1.0.99");
        Assert.Equal("1.0.11", NextVersion());     // 10 is after 9; other lines, pre-releases and other tag styles do not count
    }

    [Fact]
    public void A_new_major_minor_starts_again_at_one_and_the_result_goes_to_the_workflow_output_file()
    {
        if (Script("next-version.ps1") == null || !Has("git") || !Has("pwsh")) return;
        NewRepo("3.2", "v1.0.4", "v3.1.8");
        var output = Path.Combine(_repo, "github-output.txt");

        Assert.Equal("3.2.1", NextVersion(output));

        var lines = File.ReadAllLines(output);
        Assert.Equal(new[] { "version=3.2.1", "tag=v3.2.1" }, lines);
    }
}
