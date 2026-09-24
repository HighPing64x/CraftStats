using System.Globalization;

namespace CraftStats;

/// <summary>Single source of truth for persisting AppSettingsState inside the snapshot.</summary>
public static class SettingsMapper
{
    public static Dictionary<string, string> ToDictionary(AppSettingsState settings) => new()
    {
        [nameof(AppSettingsState.StartWithWindows)] = settings.StartWithWindows.ToString(),
        [nameof(AppSettingsState.StartMinimizedToTray)] = settings.StartMinimizedToTray.ToString(),
        [nameof(AppSettingsState.ThemePreference)] = settings.ThemePreference.ToString(),
        [nameof(AppSettingsState.CloseBehavior)] = settings.CloseBehavior.ToString(),
        [nameof(AppSettingsState.SaveMode)] = settings.SaveMode.ToString(),
        [nameof(AppSettingsState.SaveIntervalSeconds)] = settings.SaveIntervalSeconds.ToString(CultureInfo.InvariantCulture),
        [nameof(AppSettingsState.CollectKeyboardKeys)] = settings.CollectKeyboardKeys.ToString(),
        [nameof(AppSettingsState.CollectAllKeyboardKeys)] = settings.CollectAllKeyboardKeys.ToString(),
        [nameof(AppSettingsState.CollectMouseLeftButton)] = settings.CollectMouseLeftButton.ToString(),
        [nameof(AppSettingsState.CollectMouseRightButton)] = settings.CollectMouseRightButton.ToString(),
        [nameof(AppSettingsState.CollectMouseMiddleButton)] = settings.CollectMouseMiddleButton.ToString(),
        [nameof(AppSettingsState.CollectWindowTitles)] = settings.CollectWindowTitles.ToString(),
        [nameof(AppSettingsState.CollectProcessNames)] = settings.CollectProcessNames.ToString(),
        [nameof(AppSettingsState.CollectNetworkStats)] = settings.CollectNetworkStats.ToString(),
        [nameof(AppSettingsState.CollectFileStats)] = settings.CollectFileStats.ToString()
    };

    public static void ApplyTo(AppSettingsState settings, IReadOnlyDictionary<string, string> data)
    {
        if (TryBool(data, nameof(AppSettingsState.StartWithWindows), out var startWithWindows))
            settings.StartWithWindows = startWithWindows;
        if (TryBool(data, nameof(AppSettingsState.StartMinimizedToTray), out var startMinimized))
            settings.StartMinimizedToTray = startMinimized;
        if (TryEnum<AppThemePreference>(data, nameof(AppSettingsState.ThemePreference), out var theme))
            settings.ThemePreference = theme;
        if (TryEnum<CloseBehavior>(data, nameof(AppSettingsState.CloseBehavior), out var closeBehavior))
            settings.CloseBehavior = closeBehavior;
        if (TryEnum<SaveMode>(data, nameof(AppSettingsState.SaveMode), out var mode))
            settings.SaveMode = mode;
        if (TryInt(data, nameof(AppSettingsState.SaveIntervalSeconds), out var interval))
            settings.SaveIntervalSeconds = Math.Clamp(interval, 5, 3600);
        if (TryBool(data, nameof(AppSettingsState.CollectKeyboardKeys), out var keyboard))
            settings.CollectKeyboardKeys = keyboard;
        if (TryBool(data, nameof(AppSettingsState.CollectAllKeyboardKeys), out var allKeyboard))
            settings.CollectAllKeyboardKeys = allKeyboard;
        if (TryBool(data, nameof(AppSettingsState.CollectMouseLeftButton), out var mouseLeft))
            settings.CollectMouseLeftButton = mouseLeft;
        if (TryBool(data, nameof(AppSettingsState.CollectMouseRightButton), out var mouseRight))
            settings.CollectMouseRightButton = mouseRight;
        if (TryBool(data, nameof(AppSettingsState.CollectMouseMiddleButton), out var mouseMiddle))
            settings.CollectMouseMiddleButton = mouseMiddle;
        if (TryBool(data, nameof(AppSettingsState.CollectWindowTitles), out var titles))
            settings.CollectWindowTitles = titles;
        if (TryBool(data, nameof(AppSettingsState.CollectProcessNames), out var processNames))
            settings.CollectProcessNames = processNames;
        if (TryBool(data, nameof(AppSettingsState.CollectNetworkStats), out var network))
            settings.CollectNetworkStats = network;
        if (TryBool(data, nameof(AppSettingsState.CollectFileStats), out var fileStats))
            settings.CollectFileStats = fileStats;
    }

    private static bool TryBool(IReadOnlyDictionary<string, string> data, string key, out bool value)
    {
        value = default;
        return data.TryGetValue(key, out var text) && bool.TryParse(text, out value);
    }

    private static bool TryInt(IReadOnlyDictionary<string, string> data, string key, out int value)
    {
        value = default;
        return data.TryGetValue(key, out var text) && int.TryParse(text, out value);
    }

    private static bool TryEnum<TEnum>(IReadOnlyDictionary<string, string> data, string key, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return data.TryGetValue(key, out var text) && Enum.TryParse(text, out value);
    }
}
