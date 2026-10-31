using System.Text.Json;
using CinemaGuard.Core.Logging;

namespace CinemaGuard.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, atomically, with sane recovery.</summary>
public sealed class SettingsService
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaGuard", "settings.json");

    public string SettingsPath { get; }
    public AppSettings Current { get; private set; }

    public event Action? Changed;

    public SettingsService(string? path = null)
    {
        SettingsPath = path ?? DefaultPath;
        Current = Load();
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions);
                if (settings is not null)
                {
                    settings.Normalise();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read settings from {SettingsPath}; starting from defaults.", ex);
            TryQuarantine();
        }

        var created = AppSettings.CreateDefault();
        Current = created;
        TrySave(created);
        return created;
    }

    public void Save() => SaveAs(Current);

    public void SaveAs(AppSettings settings)
    {
        settings.Normalise();
        Current = settings;
        TrySave(settings);
        Changed?.Invoke();
    }

    private void TrySave(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, AppSettings.JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save settings to {SettingsPath}.", ex);
        }
    }

    private void TryQuarantine()
    {
        try
        {
            if (File.Exists(SettingsPath))
                File.Move(SettingsPath, SettingsPath + $".bad-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move the unreadable settings file aside: {ex.Message}");
        }
    }
}
