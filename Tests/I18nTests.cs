using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using XcpCam.Core;

namespace XcpCam.Tests;

internal static class I18nTests
{
    private static readonly Regex PlaceholderRx = new(@"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}", RegexOptions.Compiled);

    public static void Run(Action<bool, string> check)
    {
        var catalogs = I18n.AllCatalogs();
        string[] codes = [I18n.Zh, I18n.En, I18n.Ja, I18n.Ko];

        // 1. All four embedded catalogs load and are non-empty
        foreach(var code in codes)
            check(catalogs.TryGetValue(code, out var dict) && dict.Count > 0, $"i18n: embedded catalog '{code}' loaded ({(catalogs.TryGetValue(code, out var d) ? d.Count : 0)} keys)");

        var zh = catalogs[I18n.Zh];

        // 2. Key parity, non-empty values, placeholder parity, and valid format strings across all locales
        foreach(var code in codes)
        {
            var dict = catalogs[code];
            var missing = zh.Keys.Where(k => !dict.ContainsKey(k)).ToList();
            var extra = dict.Keys.Where(k => !zh.ContainsKey(k)).ToList();
            check(missing.Count == 0 && extra.Count == 0,
                $"i18n: '{code}' key parity with zh (missing={missing.Count}, extra={extra.Count})");

            var empty = dict.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
            check(empty.Count == 0, $"i18n: '{code}' has no empty values (empty={empty.Count})");

            int placeholderMismatches = 0;
            int formatErrors = 0;
            foreach(var (key, value) in dict)
            {
                if(zh.TryGetValue(key, out var zhVal))
                {
                    var zhPh = ExtractPlaceholderIndices(zhVal);
                    var curPh = ExtractPlaceholderIndices(value);
                    if(!zhPh.SequenceEqual(curPh)) placeholderMismatches++;
                }

                // Validate format syntax by formatting with dummy args if placeholders exist
                var indices = ExtractPlaceholderIndices(value);
                if(indices.Length > 0)
                {
                    int maxIdx = indices[^1];
                    var args = new object[maxIdx + 1];
                    for(int i = 0; i < args.Length; i++) args[i] = i;
                    try { _ = string.Format(CultureInfo.InvariantCulture, value, args); }
                    catch(FormatException) { formatErrors++; }
                }
            }

            check(placeholderMismatches == 0, $"i18n: '{code}' placeholder indices match zh (mismatches={placeholderMismatches})");
            check(formatErrors == 0, $"i18n: '{code}' all format strings valid (errors={formatErrors})");
        }

        // 3. Game language resolution (name, numeric fallback, unsupported -> Dalamud UI -> English)
        check(I18n.ResolveGameLanguage("ChineseSimplified", 4) == I18n.Zh, "i18n: ResolveGameLanguage ChineseSimplified -> zh");
        check(I18n.ResolveGameLanguage("ChineseTraditional", 5) == I18n.Zh, "i18n: ResolveGameLanguage ChineseTraditional -> zh");
        check(I18n.ResolveGameLanguage("Japanese", 0) == I18n.Ja, "i18n: ResolveGameLanguage Japanese -> ja");
        check(I18n.ResolveGameLanguage("Korean", 6) == I18n.Ko, "i18n: ResolveGameLanguage Korean -> ko");
        check(I18n.ResolveGameLanguage("English", 1) == I18n.En, "i18n: ResolveGameLanguage English -> en");
        check(I18n.ResolveGameLanguage("German", 2, "ja") == I18n.Ja, "i18n: ResolveGameLanguage German falls back to Dalamud UI language");
        check(I18n.ResolveGameLanguage("French", 3, "fr") == I18n.En, "i18n: ResolveGameLanguage French with unsupported UI falls back to en");
        check(I18n.ResolveGameLanguage(null, 0) == I18n.Ja, "i18n: ResolveGameLanguage numeric 0 -> ja");
        check(I18n.ResolveGameLanguage(null, 1) == I18n.En, "i18n: ResolveGameLanguage numeric 1 -> en");
        check(I18n.ResolveGameLanguage(null, 4) == I18n.Zh, "i18n: ResolveGameLanguage numeric 4 -> zh");
        check(I18n.ResolveGameLanguage(null, 6) == I18n.Ko, "i18n: ResolveGameLanguage numeric 6 -> ko");
        check(I18n.ResolveGameLanguage(null, 7) == I18n.Zh, "i18n: ResolveGameLanguage numeric 7 -> zh");
        check(I18n.ResolveGameLanguage(null, 99, "ko-KR") == I18n.Ko, "i18n: ResolveGameLanguage unknown numeric uses normalized UI language");
        check(I18n.ResolveGameLanguage(null, 99, null) == I18n.En, "i18n: ResolveGameLanguage unknown numeric with no UI defaults to en");

        // 4. Normalization helpers
        check(I18n.NormalizePreference(null) == I18n.Auto && I18n.NormalizePreference("") == I18n.Auto && I18n.NormalizePreference("AUTO") == I18n.Auto,
            "i18n: NormalizePreference defaults to auto");
        check(I18n.NormalizePreference("fr") == I18n.Auto, "i18n: NormalizePreference unknown falls back to auto");
        check(I18n.NormalizeLanguageCode("zh-CN") == I18n.Zh && I18n.NormalizeLanguageCode("chs") == I18n.Zh, "i18n: NormalizeLanguageCode zh variants");
        check(I18n.NormalizeLanguageCode("en-US") == I18n.En, "i18n: NormalizeLanguageCode en variants");
        check(I18n.NormalizeLanguageCode("ja-JP") == I18n.Ja && I18n.NormalizeLanguageCode("jp") == I18n.Ja, "i18n: NormalizeLanguageCode ja variants");
        check(I18n.NormalizeLanguageCode("ko-KR") == I18n.Ko && I18n.NormalizeLanguageCode("kr") == I18n.Ko, "i18n: NormalizeLanguageCode ko variants");

        // 5. Runtime switching, auto follow, LanguageChanged event, T() & Combo()
        string prevPref = I18n.Preference;
        string prevDetected = I18n.DetectedGameLanguage;
        int events = 0;
        void OnChanged() => events++;
        I18n.LanguageChanged += OnChanged;
        try
        {
            I18n.Initialize(I18n.Auto, "Japanese", 0);
            check(I18n.Preference == I18n.Auto && I18n.CurrentLanguage == I18n.Ja && I18n.DetectedGameLanguage == I18n.Ja,
                "i18n: Initialize auto follows Japanese client");
            string jaSettings = I18n.T("window.settings");
            check(jaSettings == catalogs[I18n.Ja]["window.settings"], "i18n: T() returns Japanese text");

            events = 0;
            I18n.UpdateGameLanguage("Korean", 6);
            check(I18n.CurrentLanguage == I18n.Ko && events == 1, "i18n: auto mode follows UpdateGameLanguage");

            events = 0;
            I18n.SetPreference(I18n.En);
            check(I18n.Preference == I18n.En && I18n.CurrentLanguage == I18n.En && events == 1, "i18n: SetPreference switches to explicit locale");

            events = 0;
            I18n.UpdateGameLanguage("ChineseSimplified", 4);
            check(I18n.CurrentLanguage == I18n.En && I18n.DetectedGameLanguage == I18n.Zh && events == 0,
                "i18n: explicit preference ignores UpdateGameLanguage");

            I18n.SetPreference(I18n.Auto);
            check(I18n.CurrentLanguage == I18n.Zh, "i18n: switching back to auto uses latest detected game language");

            check(I18n.T("__missing.test.key__") == "__missing.test.key__", "i18n: missing key falls back to key name");
            string combo = I18n.Combo("common.local", "common.world");
            check(combo == $"{I18n.T("common.local")}\0{I18n.T("common.world")}\0", "i18n: Combo joins with null terminators");
        }
        finally
        {
            I18n.LanguageChanged -= OnChanged;
            I18n.Initialize(prevPref, prevDetected == I18n.Zh ? "ChineseSimplified" : prevDetected, prevDetected == I18n.Zh ? 4 : 1);
        }
    }

    private static int[] ExtractPlaceholderIndices(string template)
    {
        var set = new SortedSet<int>();
        foreach(Match m in PlaceholderRx.Matches(template))
            if(int.TryParse(m.Groups[1].Value, out int idx)) set.Add(idx);
        return set.ToArray();
    }
}
