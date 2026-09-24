using Microsoft.Win32;

namespace CraftStats;

public static class StartWithWindowsService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CraftStats";
    private const string MinimizedArgument = "--minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Keeps the Run entry in sync; when startMinimized is set the command carries --minimized so boot launches go straight to the tray.</summary>
    public static void Apply(bool enabled, bool startMinimized)
    {
        var exePath = Environment.ProcessPath;
        var desired = enabled && exePath is not null
            ? $"\"{exePath}\"" + (startMinimized ? $" {MinimizedArgument}" : string.Empty)
            : null;

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        var current = key.GetValue(ValueName) as string;
        if (current == desired) return;

        if (desired is null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        else
            key.SetValue(ValueName, desired);
    }
}
