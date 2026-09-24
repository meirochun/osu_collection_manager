using System.Text.Json;

namespace OsuCollectionManager.Services;

/// <summary>Preferences kept per Windows user, outside the app folder so updates and single-file publishes don't lose them.</summary>
public sealed class UserSettings
{
    public string? OsuPath { get; set; }
}

public sealed class UserSettingsStore(IConfiguration config, ILogger<UserSettingsStore> logger)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>%LOCALAPPDATA%\OsuCollectionManager\settings.json, unless a "SettingsFile" override is configured (used by tests).</summary>
    private string FilePath { get; } = config["SettingsFile"] ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuCollectionManager", "settings.json");

    public UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not read {File}; starting with default settings", FilePath);
        }
        return new();
    }

    public void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the app works for this session, it will just ask again next time.
            logger.LogWarning(e, "Could not save {File}", FilePath);
        }
    }
}
