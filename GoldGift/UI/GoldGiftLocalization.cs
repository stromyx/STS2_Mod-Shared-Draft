using MegaCrit.Sts2.Core.Saves;

namespace GoldGift.UI;

/// <summary>
/// Localization helper for GoldGift UI.
/// Reads the game's language setting and provides localized strings.
/// Supports: zh (Chinese), en (English, default fallback).
/// </summary>
public static class GoldGiftLocalization
{
    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["en"] = new Dictionary<string, string>
        {
            ["title"]             = "Gift Gold",
            ["title_debug"]       = "Gift Gold [DEBUG]",
            ["button"]            = "🎁 Gift Gold",
            ["button_debug"]      = "🎁 Gift Gold [DEBUG]",
            ["tooltip"]           = "Send gold to another player (drag ⠿ to move)",
            ["your_gold"]         = "Your Gold: {0}",
            ["select_player"]     = "Select a player:",
            ["amount"]            = "Amount:",
            ["custom"]            = "Custom:",
            ["send"]              = "💰 Send Gold",
            ["no_players"]        = "No other players found.",
            ["not_enough"]        = "⚠ Not enough gold! You have {0}g.",
            ["invalid_amount"]    = "⚠ Please enter a valid amount.",
            ["cannot_find_char"]  = "⚠ Cannot find your character.",
            ["success"]           = "✓ Sent {0}g successfully!",
            ["failed"]            = "⚠ Failed to send gold.",
            ["received"]          = "💰 +{0}g from {1}!",
        },
        ["zh"] = new Dictionary<string, string>
        {
            ["title"]             = "赠送金币",
            ["title_debug"]       = "赠送金币 [DEBUG]",
            ["button"]            = "🎁 赠送金币",
            ["button_debug"]      = "🎁 赠送金币 [DEBUG]",
            ["tooltip"]           = "将金币赠送给其他玩家（拖拽 ⠿ 可移动）",
            ["your_gold"]         = "你的金币：{0}",
            ["select_player"]     = "选择一位玩家：",
            ["amount"]            = "金额：",
            ["custom"]            = "自定义：",
            ["send"]              = "💰 发送金币",
            ["no_players"]        = "没有找到其他玩家。",
            ["not_enough"]        = "⚠ 金币不足！你有 {0} 金币。",
            ["invalid_amount"]    = "⚠ 请输入有效金额。",
            ["cannot_find_char"]  = "⚠ 无法找到你的角色。",
            ["success"]           = "✓ 成功赠送 {0} 金币！",
            ["failed"]            = "⚠ 赠送失败。",
            ["received"]          = "💰 收到来自 {1} 的 {0} 金币！",
        }
    };

    private static string? _cachedLang;

    /// <summary>
    /// Get the current language code from the game's settings.
    /// Returns "zh" for Chinese, "en" for everything else (default).
    /// </summary>
    public static string GetCurrentLanguage()
    {
        if (_cachedLang != null)
            return _cachedLang;

        try
        {
            string? lang = SaveManager.Instance?.SettingsSave?.Language;
            if (!string.IsNullOrEmpty(lang))
            {
                // Game uses ISO codes like "zh", "en", "ja", "ko", etc.
                _cachedLang = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
                return _cachedLang;
            }
        }
        catch { /* SaveManager may not be ready yet */ }

        // Fallback: try Godot's TranslationServer
        try
        {
            string locale = Godot.TranslationServer.GetLocale();
            if (!string.IsNullOrEmpty(locale))
            {
                _cachedLang = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
                return _cachedLang;
            }
        }
        catch { /* ignore */ }

        return "en";
    }

    /// <summary>
    /// Get a localized string by key.
    /// Supports {0}, {1} format placeholders via string.Format.
    /// </summary>
    public static string Get(string key, params object[] args)
    {
        string lang = GetCurrentLanguage();

        if (Strings.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var template))
        {
            return args.Length > 0 ? string.Format(template, args) : template;
        }

        // Fallback to English
        if (Strings.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enTemplate))
        {
            return args.Length > 0 ? string.Format(enTemplate, args) : enTemplate;
        }

        return $"[{key}]";
    }

    /// <summary>
    /// Clear the cached language (call when settings might have changed).
    /// </summary>
    public static void ClearCache()
    {
        _cachedLang = null;
    }
}
