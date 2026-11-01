using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using GKME.Core;

namespace GKME.Localization;

public enum AppLanguage
{
    FollowSystem,
    English,
    SimplifiedChinese,
    TraditionalChineseHongKong,
    TraditionalChineseTaiwan,
    Russian,
    French,
}

/// <summary>Single source of truth for UI strings. XAML consumes the current
/// language through <see cref="BuildResourceDictionary"/> (DynamicResource),
/// while code-behind uses <see cref="Get"/> / <see cref="Format"/>.</summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly Localizer _instance = new();

    private readonly SettingsStore _settingsStore = new();
    private AppLanguage _selectedLanguage;
    private AppLanguage _effectiveLanguage;

    private Localizer()
    {
        var settings = _settingsStore.Load();
        _selectedLanguage = ParseLanguage(settings.Language);
        _effectiveLanguage = Resolve(_selectedLanguage);
    }

    public static Localizer Instance => _instance;

    /// <summary>The option picked by the user, which may be FollowSystem.</summary>
    public AppLanguage SelectedLanguage => _selectedLanguage;

    /// <summary>The language actually in use after resolving FollowSystem.</summary>
    public AppLanguage EffectiveLanguage => _effectiveLanguage;

    public string this[string key] => Get(key);

    public string Get(string key) =>
        Translations.Get(_effectiveLanguage).TryGetValue(key, out var value) ? value : key;

    public string Format(string key, params object[] args)
    {
        var text = Get(key);
        return args.Length == 0 ? text : string.Format(text, args);
    }

    public void SetLanguage(AppLanguage language)
    {
        if (_selectedLanguage == language && _effectiveLanguage == Resolve(language)) return;

        _selectedLanguage = language;
        _effectiveLanguage = Resolve(language);
        _settingsStore.Save(new AppSettings { Language = ToCode(language) });

        LanguageChanged?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public ResourceDictionary BuildResourceDictionary()
    {
        var dictionary = new ResourceDictionary();
        foreach (var pair in Translations.Get(_effectiveLanguage))
            dictionary[pair.Key] = pair.Value;
        return dictionary;
    }

    public event EventHandler? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static AppLanguage ParseLanguage(string? code) => code switch
    {
        "en" => AppLanguage.English,
        "zh-CN" => AppLanguage.SimplifiedChinese,
        "zh-HK" => AppLanguage.TraditionalChineseHongKong,
        "zh-TW" => AppLanguage.TraditionalChineseTaiwan,
        "ru" => AppLanguage.Russian,
        "fr" => AppLanguage.French,
        _ => AppLanguage.FollowSystem,
    };

    private static string ToCode(AppLanguage language) => language switch
    {
        AppLanguage.English => "en",
        AppLanguage.SimplifiedChinese => "zh-CN",
        AppLanguage.TraditionalChineseHongKong => "zh-HK",
        AppLanguage.TraditionalChineseTaiwan => "zh-TW",
        AppLanguage.Russian => "ru",
        AppLanguage.French => "fr",
        _ => "system",
    };

    private static AppLanguage Resolve(AppLanguage language) =>
        language == AppLanguage.FollowSystem ? DetectSystemLanguage() : language;

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>Reads the OS UI language. Windows is asked directly; the
    /// Linux desktop reports it through the POSIX locale environment
    /// variables. The app is published with invariant globalization, so
    /// CultureInfo cannot be used to detect the system language.</summary>
    private static AppLanguage DetectSystemLanguage()
    {
        if (!PlatformInfo.IsWindows)
            return DetectPosixLocale();

        try
        {
            var languageId = GetUserDefaultUILanguage();
            return languageId switch
            {
                0x0404 or 0x7C04 => AppLanguage.TraditionalChineseTaiwan,
                0x0C04 or 0x1404 => AppLanguage.TraditionalChineseHongKong,
                0x0804 or 0x1004 => AppLanguage.SimplifiedChinese,
                _ => (languageId & 0x3FF) switch
                {
                    0x04 => AppLanguage.SimplifiedChinese,
                    0x19 => AppLanguage.Russian,
                    0x0C => AppLanguage.French,
                    _ => AppLanguage.English,
                },
            };
        }
        catch
        {
            return AppLanguage.English;
        }
    }

    /// <summary>Resolves the locale from <c>LC_ALL</c> / <c>LC_MESSAGES</c> /
    /// <c>LANG</c>, in the order the POSIX locale rules prescribe.</summary>
    private static AppLanguage DetectPosixLocale()
    {
        try
        {
            var locale = Environment.GetEnvironmentVariable("LC_ALL");
            if (string.IsNullOrWhiteSpace(locale))
                locale = Environment.GetEnvironmentVariable("LC_MESSAGES");
            if (string.IsNullOrWhiteSpace(locale))
                locale = Environment.GetEnvironmentVariable("LANG");
            return ParseLocale(locale);
        }
        catch
        {
            return AppLanguage.English;
        }
    }

    private static AppLanguage ParseLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return AppLanguage.English;

        // "zh_CN.UTF-8@modifier" → "zh_cn"
        var value = locale.Split('.')[0].Split('@')[0].Replace('-', '_').ToLowerInvariant();
        if (value == "c" || value == "posix") return AppLanguage.English;

        if (value.StartsWith("zh"))
        {
            if (value.Contains("tw") || value.Contains("hant")) return AppLanguage.TraditionalChineseTaiwan;
            if (value.Contains("hk") || value.Contains("mo")) return AppLanguage.TraditionalChineseHongKong;
            return AppLanguage.SimplifiedChinese;
        }

        return value.Split('_')[0] switch
        {
            "ru" => AppLanguage.Russian,
            "fr" => AppLanguage.French,
            _ => AppLanguage.English,
        };
    }
}
