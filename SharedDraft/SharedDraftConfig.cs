using System.Text.Json;

namespace SharedDraft;

/// <summary>
/// Mod configuration for SharedDraft.
/// </summary>
public static class SharedDraftConfig
{
    private const string ConfigFileName = "config.json";

    /// <summary>
    /// Debug mode: when enabled, the mod works in single-player mode
    /// with simulated multiplayer data for testing purposes.
    /// </summary>
    public static bool DebugMode { get; private set; } = false;

    /// <summary>
    /// Number of simulated players in debug mode (including the local player).
    /// </summary>
    public static int DebugPlayerCount { get; private set; } = 2;

    /// <summary>
    /// Time in seconds that simulated AI players wait before making their selection.
    /// </summary>
    public static float DebugAiDelaySeconds { get; private set; } = 1.5f;

    /// <summary>
    /// Whether to show which player's card pool each card belongs to.
    /// </summary>
    public static bool ShowCardOwnership { get; private set; } = true;

    /// <summary>
    /// Whether to allow players to pick cards from any pool (true) or only
    /// cards from their own character's pool (false, more restrictive).
    /// Default true = full shared draft experience.
    /// </summary>
    public static bool AllowCrossClassPicks { get; private set; } = true;

    /// <summary>
    /// Timeout in seconds for the "waiting for all players to enter" phase.
    /// After this timeout, players who haven't entered the shared draft screen
    /// are considered opted-out and skipped.
    /// Only applies to the WaitingForReady phase — once all players enter,
    /// the Selecting phase has NO time limit.
    /// </summary>
    public static int ReadyTimeoutSeconds { get; private set; } = 60;

    public static void LoadConfig()
    {
        try
        {
            string? configDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);

            if (string.IsNullOrEmpty(configDir))
            {
                configDir = Path.Combine(
                    AppContext.BaseDirectory, "mods", ModEntry.ModId);
            }

            string configPath = Path.Combine(configDir, ConfigFileName);

            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<ConfigData>(json);

                if (config != null)
                {
                    DebugMode = config.DebugMode;
                    DebugPlayerCount = Math.Clamp(config.DebugPlayerCount, 2, 4);
                    DebugAiDelaySeconds = Math.Clamp(config.DebugAiDelaySeconds, 0.5f, 10f);
                    ShowCardOwnership = config.ShowCardOwnership;
                    AllowCrossClassPicks = config.AllowCrossClassPicks;
                    ReadyTimeoutSeconds = Math.Clamp(config.ReadyTimeoutSeconds, 10, 300);
                }

                ModEntry.Logger.Info($"Config loaded: DebugMode={DebugMode}, ShowOwnership={ShowCardOwnership}");
            }
            else
            {
                WriteDefaultConfig(configPath);
                ModEntry.Logger.Info("Default config created.");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Error($"Failed to load config: {ex.Message}");
        }
    }

    private static void WriteDefaultConfig(string path)
    {
        var config = new ConfigData
        {
            DebugMode = DebugMode,
            DebugPlayerCount = DebugPlayerCount,
            DebugAiDelaySeconds = DebugAiDelaySeconds,
            ShowCardOwnership = ShowCardOwnership,
            AllowCrossClassPicks = AllowCrossClassPicks,
            ReadyTimeoutSeconds = ReadyTimeoutSeconds
        };

        string json = JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json);
    }

    // ─── Setters for ModConfig callbacks ──────────────────────────

    internal static void SetDebugMode(bool value)
    {
        DebugMode = value;
    }

    internal static void SetDebugPlayerCount(int value)
    {
        DebugPlayerCount = Math.Clamp(value, 2, 4);
    }

    internal static void SetDebugAiDelaySeconds(float value)
    {
        DebugAiDelaySeconds = Math.Clamp(value, 0.5f, 10f);
    }

    internal static void SetShowCardOwnership(bool value)
    {
        ShowCardOwnership = value;
    }

    internal static void SetAllowCrossClassPicks(bool value)
    {
        AllowCrossClassPicks = value;
    }

    internal static void SetReadyTimeoutSeconds(int value)
    {
        ReadyTimeoutSeconds = Math.Clamp(value, 10, 300);
    }

    private class ConfigData
    {
        public bool DebugMode { get; set; } = false;
        public int DebugPlayerCount { get; set; } = 2;
        public float DebugAiDelaySeconds { get; set; } = 1.5f;
        public bool ShowCardOwnership { get; set; } = true;
        public bool AllowCrossClassPicks { get; set; } = true;
        public int ReadyTimeoutSeconds { get; set; } = 60;
    }
}
