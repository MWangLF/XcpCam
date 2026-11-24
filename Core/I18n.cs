using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace XcpCam.Core;

/// <summary>
/// Embedded JSON i18n catalog supporting Simplified Chinese (zh), English (en), Japanese (ja), and Korean (ko).
/// Default preference "auto" follows the FFXIV game client's language (falling back to Dalamud UI language, then English).
/// </summary>
public static class I18n
{
    public const string Auto = "auto";
    public const string Zh = "zh";
    public const string En = "en";
    public const string Ja = "ja";
    public const string Ko = "ko";

    public static readonly (string Code, string NativeName)[] SupportedLanguages =
    [
        (Zh, "简体中文"),
        (En, "English"),
        (Ja, "日本語"),
        (Ko, "한국어"),
    ];

    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = LoadAllCatalogs();
    private static readonly Dictionary<string, string> FallbackEn = GetCatalog(En);
    private static readonly Dictionary<string, string> FallbackZh = GetCatalog(Zh);
    private static Dictionary<string, string> _current = GetCatalog(Zh);

    /// <summary>Configured preference: "auto", "zh", "en", "ja", or "ko".</summary>
    public static string Preference { get; private set; } = Auto;

    /// <summary>Active resolved locale code: "zh", "en", "ja", or "ko".</summary>
    public static string CurrentLanguage { get; private set; } = Zh;

    /// <summary>Locale resolved from the game client.</summary>
    public static string DetectedGameLanguage { get; private set; } = Zh;

    /// <summary>Raised whenever the active resolved language changes.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Initializes the catalog with the saved user preference and detected game/UI language.</summary>
    public static void Initialize(string? preference, string? clientLanguageName, int clientLanguageValue, string? uiLanguage = null)
    {
        DetectedGameLanguage = ResolveGameLanguage(clientLanguageName, clientLanguageValue, uiLanguage);
        SetPreference(preference);
    }

    /// <summary>Updates the detected game/UI language (e.g. if Dalamud language changes) and re-applies when on "auto".</summary>
    public static void UpdateGameLanguage(string? clientLanguageName, int clientLanguageValue, string? uiLanguage = null)
    {
        DetectedGameLanguage = ResolveGameLanguage(clientLanguageName, clientLanguageValue, uiLanguage);
        if(Preference == Auto) ApplyResolved(DetectedGameLanguage);
    }

    /// <summary>Sets the user preference ("auto", "zh", "en", "ja", "ko") and activates the corresponding locale.</summary>
    public static void SetPreference(string? preference)
    {
        Preference = NormalizePreference(preference);
        ApplyResolved(Preference == Auto ? DetectedGameLanguage : Preference);
    }

    /// <summary>Directly switches active language (primarily for tests or explicit overrides).</summary>
    public static void SetLanguage(string code)
    {
        string normalized = NormalizeLanguageCode(code) ?? En;
        Preference = normalized;
        ApplyResolved(normalized);
    }

    public static string NormalizePreference(string? preference)
    {
        if(string.IsNullOrWhiteSpace(preference)) return Auto;
        var p = preference.Trim();
        if(p.Equals(Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
        return NormalizeLanguageCode(p) ?? Auto;
    }

    public static string? NormalizeLanguageCode(string? code)
    {
        if(string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        if(c == Zh || c.StartsWith("zh-") || c.StartsWith("zh_") || c is "chs" or "cht" or "cn" or "tc" or "sc") return Zh;
        if(c == En || c.StartsWith("en-") || c.StartsWith("en_")) return En;
        if(c == Ja || c == "jp" || c.StartsWith("ja-") || c.StartsWith("ja_")) return Ja;
        if(c == Ko || c == "kr" || c.StartsWith("ko-") || c.StartsWith("ko_")) return Ko;
        return null;
    }

    /// <summary>
    /// Maps Dalamud's <c>ClientLanguage</c> (by name and numeric value, avoiding compile-time coupling to CN-fork-only enum members)
    /// to a supported locale code. Unsupported client languages (German/French) fall back to Dalamud UI language, then English.
    /// </summary>
    public static string ResolveGameLanguage(string? clientLanguageName, int clientLanguageValue, string? uiLanguage = null)
    {
        if(!string.IsNullOrWhiteSpace(clientLanguageName))
        {
            var name = clientLanguageName.Trim();
            if(name.Contains("Chinese", StringComparison.OrdinalIgnoreCase)) return Zh;
            if(name.Equals("Japanese", StringComparison.OrdinalIgnoreCase)) return Ja;
            if(name.Equals("Korean", StringComparison.OrdinalIgnoreCase)) return Ko;
            if(name.Equals("English", StringComparison.OrdinalIgnoreCase)) return En;
            if(name.Equals("German", StringComparison.OrdinalIgnoreCase) || name.Equals("French", StringComparison.OrdinalIgnoreCase))
                return NormalizeLanguageCode(uiLanguage) ?? En;
        }

        // Standard FFXIV ClientLanguage numeric IDs: 0=Japanese, 1=English, 2=German, 3=French, 4=ChineseSimplified, 5=ChineseTraditional, 6=Korean
        return clientLanguageValue switch
        {
            0 => Ja,
            1 => En,
            4 or 5 or 7 => Zh,
            6 => Ko,
            _ => NormalizeLanguageCode(uiLanguage) ?? En,
        };
    }

    public static string NativeName(string code)
    {
        foreach(var (c, name) in SupportedLanguages)
            if(c == code) return name;
        return code;
    }

    /// <summary>Returns the localized string for <paramref name="key"/>, falling back to English, Chinese, then the key itself.</summary>
    public static string T(string key)
    {
        if(_current.TryGetValue(key, out var value)) return value;
        if(FallbackEn.TryGetValue(key, out value)) return value;
        if(FallbackZh.TryGetValue(key, out value)) return value;
        return key;
    }

    /// <summary>Returns the localized string formatted with <see cref="CultureInfo.InvariantCulture"/>.</summary>
    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        if(args.Length == 0) return template;
        try { return string.Format(CultureInfo.InvariantCulture, template, args); }
        catch(FormatException) { return template; }
    }

    /// <summary>Builds a null-separated string for ImGui.Combo from translation keys.</summary>
    public static string Combo(params string[] keys)
    {
        var parts = new string[keys.Length];
        for(int i = 0; i < keys.Length; i++) parts[i] = T(keys[i]);
        return string.Join('\0', parts) + "\0";
    }

    /// <summary>Exposes loaded catalogs for automated validation tests.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AllCatalogs()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(Catalogs.Count);
        foreach(var (k, v) in Catalogs) result[k] = v;
        return result;
    }

    private static void ApplyResolved(string resolved)
    {
        var next = Catalogs.ContainsKey(resolved) ? resolved : En;
        bool changed = CurrentLanguage != next;
        CurrentLanguage = next;
        _current = GetCatalog(next);
        if(changed) LanguageChanged?.Invoke();
    }

    private static Dictionary<string, string> GetCatalog(string code)
        => Catalogs.TryGetValue(code, out var dict) ? dict : new Dictionary<string, string>();

    private static Dictionary<string, Dictionary<string, string>> LoadAllCatalogs()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(I18n).Assembly;
        var names = asm.GetManifestResourceNames();

        foreach(var (code, _) in SupportedLanguages)
        {
            string suffix = $".Locales.{code}.json";
            string? resName = null;
            foreach(var n in names)
            {
                if(n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
            }
            if(resName == null)
            {
                result[code] = new Dictionary<string, string>();
                continue;
            }
            using var stream = asm.GetManifestResourceStream(resName);
            if(stream == null)
            {
                result[code] = new Dictionary<string, string>();
                continue;
            }
            using var reader = new StreamReader(stream);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
            result[code] = parsed ?? new Dictionary<string, string>();
        }

        return result;
    }
}
