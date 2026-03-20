using System;
using System.Collections.Generic;
using System.Reflection;

namespace GoldGift;

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
            // ── Header: 基本设置 ──
            MakeEntry(key: null, label: "Basic Settings", type: ConfigTypeValue("Header"),
                labels: new Dictionary<string, string> { { "en", "Basic Settings" }, { "zhs", "基本设置" } }),

            // 默认赠送金额
            MakeEntry("DefaultGiftAmount", "Default Gift Amount", ConfigTypeValue("Slider"),
                defaultValue: (float)GoldGiftConfig.DefaultGiftAmount,
                min: 1f, max: 200f, step: 1f, format: "F0",
                labels: new Dictionary<string, string>
                    { { "en", "Default Gift Amount" }, { "zhs", "默认赠送金额" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Default gold amount for quick gift buttons" }, { "zhs", "快速赠送按钮的默认金额" } },
                onChanged: v => GoldGiftConfig.SetDefaultGiftAmount((int)(float)v)),

            // 最小赠送金额
            MakeEntry("MinGiftAmount", "Min Gift Amount", ConfigTypeValue("Slider"),
                defaultValue: (float)GoldGiftConfig.MinGiftAmount,
                min: 1f, max: 50f, step: 1f, format: "F0",
                labels: new Dictionary<string, string>
                    { { "en", "Min Gift Amount" }, { "zhs", "最小赠送金额" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Minimum gold that can be gifted" }, { "zhs", "单次赠送的最小金额" } },
                onChanged: v => GoldGiftConfig.SetMinGiftAmount((int)(float)v)),

            // 最大赠送金额
            MakeEntry("MaxGiftAmount", "Max Gift Amount", ConfigTypeValue("Slider"),
                defaultValue: (float)GoldGiftConfig.MaxGiftAmount,
                min: 50f, max: 9999f, step: 50f, format: "F0",
                labels: new Dictionary<string, string>
                    { { "en", "Max Gift Amount" }, { "zhs", "最大赠送金额" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Maximum gold per transaction" }, { "zhs", "单次赠送的最大金额" } },
                onChanged: v => GoldGiftConfig.SetMaxGiftAmount((int)(float)v)),

            // ── Separator ──
            MakeEntry(key: null, label: "", type: ConfigTypeValue("Separator")),

            // ── Header: 调试设置 ──
            MakeEntry(key: null, label: "Debug Settings", type: ConfigTypeValue("Header"),
                labels: new Dictionary<string, string> { { "en", "Debug Settings" }, { "zhs", "调试设置" } }),

            // 调试模式开关
            MakeEntry("DebugMode", "Debug Mode", ConfigTypeValue("Toggle"),
                defaultValue: GoldGiftConfig.DebugMode,
                labels: new Dictionary<string, string>
                    { { "en", "Debug Mode" }, { "zhs", "调试模式" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Enable single-player debug with simulated players" }, { "zhs", "启用单人调试模式（模拟多人）" } },
                onChanged: v => GoldGiftConfig.SetDebugMode((bool)v)),

            // 调试玩家数量
            MakeEntry("DebugPlayerCount", "Debug Player Count", ConfigTypeValue("Slider"),
                defaultValue: (float)GoldGiftConfig.DebugPlayerCount,
                min: 2f, max: 8f, step: 1f, format: "F0",
                labels: new Dictionary<string, string>
                    { { "en", "Debug Player Count" }, { "zhs", "调试玩家数量" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Number of simulated players (including local)" }, { "zhs", "模拟玩家数量（含本地玩家）" } },
                onChanged: v => GoldGiftConfig.SetDebugPlayerCount((int)(float)v)),

            // 调试起始金币
            MakeEntry("DebugStartingGold", "Debug Starting Gold", ConfigTypeValue("Slider"),
                defaultValue: (float)GoldGiftConfig.DebugStartingGold,
                min: 0f, max: 9999f, step: 50f, format: "F0",
                labels: new Dictionary<string, string>
                    { { "en", "Debug Starting Gold" }, { "zhs", "调试起始金币" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Gold for simulated players in debug mode" }, { "zhs", "调试模式下模拟玩家的起始金币" } },
                onChanged: v => GoldGiftConfig.SetDebugStartingGold((int)(float)v)),
        };

        // Create a properly-typed ConfigEntry[] array (not object[])
        var entries = Array.CreateInstance(_entryType!, entryList.Count);
        for (int i = 0; i < entryList.Count; i++)
            entries.SetValue(entryList[i], i);

        var register = _apiType!.GetMethod("Register", new[] { typeof(string), typeof(string), entries.GetType() });
        register?.Invoke(null, new object[] { ModEntry.ModId, "Gold Gift 💰", entries });

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
