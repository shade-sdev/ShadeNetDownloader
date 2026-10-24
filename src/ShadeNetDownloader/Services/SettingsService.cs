using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadeNetDownloader.Services;

/// <summary>Loads and saves <see cref="AppSettings"/>. Writes are atomic (temp file + replace) so a crash never leaves half a file.</summary>
public sealed class SettingsService
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    readonly object _gate = new();

    public SettingsService(string? dataDir = null)
    {
        DataDir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadeNet");
        Directory.CreateDirectory(DataDir);
        SettingsPath = Path.Combine(DataDir, "settings.json");
        DatabasePath = Path.Combine(DataDir, "shadenet.db");
    }

    public string DataDir { get; }
    public string SettingsPath { get; }
    public string DatabasePath { get; }
    public AppSettings Current { get; private set; } = new();

    /// <summary>Reads the settings file; a missing or damaged file falls back to defaults (the bad file is kept as .bad).</summary>
    public AppSettings Load()
    {
        AppSettings loaded;
        try
        {
            loaded = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try
            {
                File.Copy(SettingsPath, SettingsPath + ".bad", overwrite: true);
            }
            catch
            {
                // nothing more we can do
            }

            loaded = new AppSettings();
        }

        // JSON loading replaces the dictionaries (losing their case-insensitive comparers) and trusts every value:
        // put the comparers back and clamp what must stay in range.
        loaded.Engine.Normalize();
        loaded.Appearance ??= new AppearanceSettings();
        loaded.Appearance.Normalize();
        loaded.Engine.ToolsFolder = Path.Combine(DataDir, "tools");

        Current = loaded;
        return loaded;
    }

    public void Save()
    {
        lock (_gate)
        {
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null);
            else File.Move(tmp, SettingsPath);
        }
    }
}
