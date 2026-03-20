using System.Text.Json;

namespace StartingGold;

/// <summary>
/// Mod configuration for StartingGold.
/// </summary>
public static class StartingGoldConfig
{
    private const string ConfigFileName = "config.json";

    /// <summary>
    /// Starting gold amount for each player.
    /// </summary>
    public static int GoldAmount { get; private set; } = 900;

    public static void LoadConfig()
    {
        try
        {
            string? configDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);

            if (string.IsNullOrEmpty(configDir))
            {
                configDir = Path.Combine(
                    AppContext.BaseDirectory, "mods", "StartingGold");
            }

            string configPath = Path.Combine(configDir, ConfigFileName);

            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<ConfigData>(json);

                if (config != null)
                {
                    GoldAmount = Math.Clamp(config.GoldAmount, 0, 9999);
                }

                Godot.GD.Print($"[StartingGold] Config loaded: GoldAmount={GoldAmount}");
            }
            else
            {
                WriteDefaultConfig(configPath);
                Godot.GD.Print("[StartingGold] Default config created.");
            }
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[StartingGold] Failed to load config: {ex.Message}");
        }
    }

    // ─── Setters for ModConfig callbacks ──────────────────────────

    internal static void SetGoldAmount(int value)
    {
        GoldAmount = Math.Clamp(value, 0, 9999);
    }

    // ─── Private helpers ─────────────────────────────────────────

    private static void WriteDefaultConfig(string path)
    {
        var config = new ConfigData { GoldAmount = GoldAmount };

        string json = JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json);
    }

    private class ConfigData
    {
        public int GoldAmount { get; set; } = 900;
    }
}
