using System;
using System.Collections.Generic;
using System.Reflection;

namespace SharedDraft;

/// <summary>
/// Weak-dependency bridge to ModConfig.
/// Works via reflection — no DLL reference needed.
/// If ModConfig is not installed, the mod still works normally.
/// </summary>
internal static class ModConfigBridge
{
    private static bool _detected;
    private static bool _available;
    private static bool _registered;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configType;

    internal static bool IsAvailable
    {
        get
        {
            if (!_detected)
            {
                _detected = true;
                _apiType = Type.GetType("ModConfig.ModConfigApi, ModConfig");
                _entryType = Type.GetType("ModConfig.ConfigEntry, ModConfig");
                _configType = Type.GetType("ModConfig.ConfigType, ModConfig");
                _available = _apiType != null && _entryType != null && _configType != null;
            }
            return _available;
        }
    }

    internal static void Register()
    {
        if (_registered) return;
        _registered = true;

        if (!IsAvailable)
        {
            ModEntry.Logger.Info("ModConfig not detected, skipping registration.");
            return;
        }
        try
        {
            DoRegister();
        }
        catch (Exception e)
        {
            ModEntry.Logger.Error($"ModConfig registration failed: {e}");
        }
    }

    private static void DoRegister()
    {
        var entryList = new List<object>
        {
            // ── Header: 游戏设置 ──
            MakeEntry(key: null, label: "Game Settings", type: ConfigTypeValue("Header"),
                labels: new Dictionary<string, string> { { "en", "Game Settings" }, { "zhs", "游戏设置" } }),

            // 显示卡牌归属
            MakeEntry("ShowCardOwnership", "Show Card Ownership", ConfigTypeValue("Toggle"),
                defaultValue: SharedDraftConfig.ShowCardOwnership,
                labels: new Dictionary<string, string>
                    { { "en", "Show Card Ownership" }, { "zhs", "显示卡牌归属" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Show which player's card pool each card belongs to" }, { "zhs", "显示每张卡牌属于哪个玩家的卡池" } },
                onChanged: v => SharedDraftConfig.SetShowCardOwnership((bool)v)),

            // 允许跨职业选卡
            MakeEntry("AllowCrossClassPicks", "Allow Cross-Class Picks", ConfigTypeValue("Toggle"),
                defaultValue: SharedDraftConfig.AllowCrossClassPicks,
                labels: new Dictionary<string, string>
                    { { "en", "Allow Cross-Class Picks" }, { "zhs", "允许跨职业选卡" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Allow picking cards from any player's pool (true = full shared draft)" },
                      { "zhs", "允许从任意玩家的卡池中选卡（开启 = 完全共享轮抽）" } },
                onChanged: v => SharedDraftConfig.SetAllowCrossClassPicks((bool)v)),

            // ── Separator ──
            MakeEntry(key: null, label: "", type: ConfigTypeValue("Separator")),

            // ── Header: 调试设置 ──
            MakeEntry(key: null, label: "Debug Settings", type: ConfigTypeValue("Header"),
                labels: new Dictionary<string, string> { { "en", "Debug Settings" }, { "zhs", "调试设置" } }),

            // 调试模式开关
            MakeEntry("DebugMode", "Debug Mode", ConfigTypeValue("Toggle"),
                defaultValue: SharedDraftConfig.DebugMode,
                labels: new Dictionary<string, string>
                    { { "en", "Debug Mode" }, { "zhs", "调试模式" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Enable single-player debug with simulated AI players" }, { "zhs", "启用单人调试模式（AI 模拟多人）" } },
                onChanged: v => SharedDraftConfig.SetDebugMode((bool)v)),

            // 调试玩家数量
            MakeEntry("DebugPlayerCount", "Debug Player Count", ConfigTypeValue("Slider"),
                defaultValue: (float)SharedDraftConfig.DebugPlayerCount,
                min: 2f, max: 4f, step: 1f, format: "F0",
                labels: new Dictionary<string, string>
                    { { "en", "Debug Player Count" }, { "zhs", "调试玩家数量" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Number of simulated players (including local)" }, { "zhs", "模拟玩家数量（含本地玩家）" } },
                onChanged: v => SharedDraftConfig.SetDebugPlayerCount((int)(float)v)),

            // AI 延迟
            MakeEntry("DebugAiDelaySeconds", "AI Delay (seconds)", ConfigTypeValue("Slider"),
                defaultValue: SharedDraftConfig.DebugAiDelaySeconds,
                min: 0.5f, max: 10f, step: 0.5f, format: "F1",
                labels: new Dictionary<string, string>
                    { { "en", "AI Delay (seconds)" }, { "zhs", "AI 延迟（秒）" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "How long simulated AI waits before picking" }, { "zhs", "模拟 AI 选卡前的等待时间" } },
                onChanged: v => SharedDraftConfig.SetDebugAiDelaySeconds((float)v)),
        };

        // Create a properly-typed ConfigEntry[] array (not object[])
        var entries = Array.CreateInstance(_entryType!, entryList.Count);
        for (int i = 0; i < entryList.Count; i++)
            entries.SetValue(entryList[i], i);

        var register = _apiType!.GetMethod("Register", new[] { typeof(string), typeof(string), entries.GetType() });
        register?.Invoke(null, new object[] { ModEntry.ModId, "Shared Draft 🃏", entries });

        ModEntry.Logger.Info("Registered with ModConfig successfully.");
    }

    // ─── Reflection Helpers ──────────────────────────────────────

    private static object ConfigTypeValue(string name) => Enum.Parse(_configType!, name);

    private static object MakeEntry(
        string? key, string label, object type,
        object? defaultValue = null,
        float min = 0, float max = 100, float step = 1, string format = "F0",
        string[]? options = null,
        Action<object>? onChanged = null,
        Dictionary<string, string>? labels = null,
        Dictionary<string, string>? descriptions = null)
    {
        var entry = Activator.CreateInstance(_entryType!)!;
        if (key != null) SetProp(entry, "Key", key);
        SetProp(entry, "Label", label);
        SetProp(entry, "Type", type);

        if (defaultValue != null) SetProp(entry, "DefaultValue", defaultValue);
        SetProp(entry, "Min", min);
        SetProp(entry, "Max", max);
        SetProp(entry, "Step", step);
        SetProp(entry, "Format", format);

        if (options != null) SetProp(entry, "Options", options);
        if (onChanged != null) SetProp(entry, "OnChanged", onChanged);
        if (labels != null) SetProp(entry, "Labels", labels);
        if (descriptions != null) SetProp(entry, "Descriptions", descriptions);

        return entry;
    }

    private static void SetProp(object obj, string name, object value)
    {
        obj.GetType().GetProperty(name)?.SetValue(obj, value);
    }

    internal static T GetValue<T>(string modId, string key, T fallback)
    {
        if (!IsAvailable) return fallback;
        try
        {
            var method = _apiType!.GetMethod("GetValue")!.MakeGenericMethod(typeof(T));
            return (T)method.Invoke(null, new object[] { modId, key })!;
        }
        catch
        {
            return fallback;
        }
    }
}
