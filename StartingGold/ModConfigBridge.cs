using System;
using System.Collections.Generic;
using System.Reflection;

namespace StartingGold;

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
            Godot.GD.Print("[StartingGold] ModConfig not detected, skipping registration.");
            return;
        }
        try
        {
            DoRegister();
        }
        catch (Exception e)
        {
            Godot.GD.PrintErr($"[StartingGold] ModConfig registration failed: {e}");
        }
    }

    private static void DoRegister()
    {
        var entryList = new List<object>
        {
            // ── Header: 金币设置 ──
            MakeEntry(key: null, label: "Gold Settings", type: ConfigTypeValue("Header"),
                labels: new Dictionary<string, string> { { "en", "Gold Settings" }, { "zhs", "金币设置" } }),

            // 起始金币数量
            MakeEntry("StartingGoldAmount", "Starting Gold", ConfigTypeValue("Slider"),
                defaultValue: (float)StartingGoldConfig.GoldAmount,
                min: 0f, max: 9999f, step: 50f, format: "F0",
                labels: new Dictionary<string, string>
                    { { "en", "Starting Gold" }, { "zhs", "起始金币" } },
                descriptions: new Dictionary<string, string>
                    { { "en", "Gold amount each player starts with" }, { "zhs", "每个玩家的初始金币数量" } },
                onChanged: v => StartingGoldConfig.SetGoldAmount((int)(float)v)),
        };

        // Create a properly-typed ConfigEntry[] array (not object[])
        var entries = Array.CreateInstance(_entryType!, entryList.Count);
        for (int i = 0; i < entryList.Count; i++)
            entries.SetValue(entryList[i], i);

        var register = _apiType!.GetMethod("Register", new[] { typeof(string), typeof(string), entries.GetType() });
        register?.Invoke(null, new object[] { "StartingGold", "Starting Gold 🪙", entries });

        Godot.GD.Print("[StartingGold] Registered with ModConfig successfully.");
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
