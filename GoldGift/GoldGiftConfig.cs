using System.Text.Json;

namespace GoldGift;

/// <summary>
/// Mod configuration for Gold Gift.
/// </summary>
public static class GoldGiftConfig
{
    private const string ConfigFileName = "config.json";

    /// <summary>
    /// Default gold amount for quick gift.
    /// </summary>
    public static int DefaultGiftAmount { get; private set; } = 25;

    /// <summary>
    /// Minimum gold amount that can be gifted.
    /// </summary>
    public static int MinGiftAmount { get; private set; } = 1;

    /// <summary>
    /// Maximum gold amount that can be gifted per transaction.
    /// </summary>
    public static int MaxGiftAmount { get; private set; } = 999;

    /// <summary>
    /// Available quick-gift amount presets.
    /// </summary>
    public static int[] QuickAmounts { get; private set; } = [10, 25, 50, 100];

    /// <summary>
    /// Debug mode: when enabled, the mod works in single-player mode
    /// with simulated multiplayer data for testing purposes.
    /// Set to true in config.json to enable.
    /// </summary>
    public static bool DebugMode { get; private set; } = false;

    /// <summary>
    /// Number of simulated players in debug mode (including the local player).
    /// </summary>
    public static int DebugPlayerCount { get; private set; } = 3;

    /// <summary>
    /// Starting gold for simulated players in debug mode.
    /// </summary>
    public static int DebugStartingGold { get; private set; } = 200;

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
                    DefaultGiftAmount = Math.Clamp(config.DefaultGiftAmount, 1, 999);
                    MinGiftAmount = Math.Clamp(config.MinGiftAmount, 1, 100);
                    MaxGiftAmount = Math.Clamp(config.MaxGiftAmount, 1, 9999);
                    DebugMode = config.DebugMode;
                    DebugPlayerCount = Math.Clamp(config.DebugPlayerCount, 2, 8);
                    DebugStartingGold = Math.Clamp(config.DebugStartingGold, 0, 9999);

                    if (config.QuickAmounts is { Length: > 0 })
                        QuickAmounts = config.QuickAmounts;
                }

                ModEntry.Logger.Info($"Config loaded: default={DefaultGiftAmount}");
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
            DefaultGiftAmount = DefaultGiftAmount,
            MinGiftAmount = MinGiftAmount,
            MaxGiftAmount = MaxGiftAmount,
            QuickAmounts = QuickAmounts,
            DebugMode = DebugMode,
            DebugPlayerCount = DebugPlayerCount,
            DebugStartingGold = DebugStartingGold
        };

        string json = JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json);
    }

    // ─── Setters for ModConfig callbacks ──────────────────────────

    internal static void SetDefaultGiftAmount(int value)
    {
        DefaultGiftAmount = Math.Clamp(value, 1, 999);
    }

    internal static void SetMinGiftAmount(int value)
    {
        MinGiftAmount = Math.Clamp(value, 1, 100);
    }

    internal static void SetMaxGiftAmount(int value)
    {
        MaxGiftAmount = Math.Clamp(value, 1, 9999);
    }

    internal static void SetDebugMode(bool value)
    {
        DebugMode = value;
    }

    internal static void SetDebugPlayerCount(int value)
    {
        DebugPlayerCount = Math.Clamp(value, 2, 8);
    }

    internal static void SetDebugStartingGold(int value)
    {
        DebugStartingGold = Math.Clamp(value, 0, 9999);
    }

    private class ConfigData
    {
        public int DefaultGiftAmount { get; set; } = 25;
        public int MinGiftAmount { get; set; } = 1;
        public int MaxGiftAmount { get; set; } = 999;
        public int[]? QuickAmounts { get; set; }
        public bool DebugMode { get; set; } = false;
        public int DebugPlayerCount { get; set; } = 3;
        public int DebugStartingGold { get; set; } = 200;
    }
}
