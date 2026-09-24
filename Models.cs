using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CraftStats;

public enum SaveMode
{
    Realtime,
    Interval,
    OnSuspendOrShutdown
}

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public enum CloseBehavior
{
    Ask,
    MinimizeToTray,
    Exit
}

public sealed class StatEntry : INotifyPropertyChanged
{
    private long _count;
    private TimeSpan _duration;

    public string Name { get; }

    public long Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (_duration == value) return;
            _duration = value;
            OnPropertyChanged();
        }
    }

    public string DisplayValue => Duration > TimeSpan.Zero ? DurationFormat.Format(Duration) : Count.ToString("N0");

    public StatEntry(string name)
    {
        Name = name;
    }

    public void IncrementCount(long delta = 1)
    {
        Count += delta;
        OnPropertyChanged(nameof(DisplayValue));
    }

    public void AddDuration(TimeSpan delta)
    {
        Duration += delta;
        OnPropertyChanged(nameof(DisplayValue));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SessionRecord
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.Now) - StartedAt;
}

public readonly record struct DailyUptimePoint(DateOnly Date, TimeSpan Duration);

public static class DurationFormat
{
    public static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours:0}h {value.Minutes:00}m {value.Seconds:00}s"
        : $"{value.Minutes:00}m {value.Seconds:00}s";
}

public sealed class ProcessStat
{
    public string Name { get; set; } = string.Empty;
    public long LaunchCount { get; set; }
    public TimeSpan TotalRunningTime { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class CraftStatsSnapshot
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<string, long> Counters { get; set; } = new();
    public Dictionary<string, TimeSpan> Durations { get; set; } = new();
    public Dictionary<string, ProcessStat> Processes { get; set; } = new();
    public List<SessionRecord> Sessions { get; set; } = new();
    public Dictionary<string, string> Settings { get; set; } = new();
}

public sealed class AppSettingsState : INotifyPropertyChanged
{
    private bool _startWithWindows = true;
    private bool _startMinimizedToTray;
    private AppThemePreference _themePreference = AppThemePreference.System;
    private CloseBehavior _closeBehavior = CloseBehavior.Ask;
    private SaveMode _saveMode = SaveMode.Interval;
    private int _saveIntervalSeconds = 60;
    private bool _collectKeyboardKeys = true;
    private bool _collectAllKeyboardKeys = true;
    private bool _collectMouseLeftButton = true;
    private bool _collectMouseRightButton = true;
    private bool _collectMouseMiddleButton = true;
    private bool _collectWindowTitles = true;
    private bool _collectProcessNames = true;
    private bool _collectNetworkStats = true;
    private bool _collectFileStats = true;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { if (_startWithWindows == value) return; _startWithWindows = value; OnPropertyChanged(); }
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimizedToTray;
        set { if (_startMinimizedToTray == value) return; _startMinimizedToTray = value; OnPropertyChanged(); }
    }

    public AppThemePreference ThemePreference
    {
        get => _themePreference;
        set { if (_themePreference == value) return; _themePreference = value; OnPropertyChanged(); }
    }

    public CloseBehavior CloseBehavior
    {
        get => _closeBehavior;
        set { if (_closeBehavior == value) return; _closeBehavior = value; OnPropertyChanged(); }
    }

    public SaveMode SaveMode
    {
        get => _saveMode;
        set { if (_saveMode == value) return; _saveMode = value; OnPropertyChanged(); }
    }

    public int SaveIntervalSeconds
    {
        get => _saveIntervalSeconds;
        set { if (_saveIntervalSeconds == value) return; _saveIntervalSeconds = value; OnPropertyChanged(); }
    }

    public bool CollectKeyboardKeys
    {
        get => _collectKeyboardKeys;
        set { if (_collectKeyboardKeys == value) return; _collectKeyboardKeys = value; OnPropertyChanged(); }
    }

    public bool CollectAllKeyboardKeys
    {
        get => _collectAllKeyboardKeys;
        set { if (_collectAllKeyboardKeys == value) return; _collectAllKeyboardKeys = value; OnPropertyChanged(); }
    }

    public bool CollectMouseLeftButton
    {
        get => _collectMouseLeftButton;
        set { if (_collectMouseLeftButton == value) return; _collectMouseLeftButton = value; OnPropertyChanged(); }
    }

    public bool CollectMouseRightButton
    {
        get => _collectMouseRightButton;
        set { if (_collectMouseRightButton == value) return; _collectMouseRightButton = value; OnPropertyChanged(); }
    }

    public bool CollectMouseMiddleButton
    {
        get => _collectMouseMiddleButton;
        set { if (_collectMouseMiddleButton == value) return; _collectMouseMiddleButton = value; OnPropertyChanged(); }
    }

    public bool CollectWindowTitles
    {
        get => _collectWindowTitles;
        set { if (_collectWindowTitles == value) return; _collectWindowTitles = value; OnPropertyChanged(); }
    }

    public bool CollectProcessNames
    {
        get => _collectProcessNames;
        set { if (_collectProcessNames == value) return; _collectProcessNames = value; OnPropertyChanged(); }
    }

    public bool CollectNetworkStats
    {
        get => _collectNetworkStats;
        set { if (_collectNetworkStats == value) return; _collectNetworkStats = value; OnPropertyChanged(); }
    }

    public bool CollectFileStats
    {
        get => _collectFileStats;
        set { if (_collectFileStats == value) return; _collectFileStats = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DashboardState : INotifyPropertyChanged
{
    public ObservableCollection<StatEntry> Items { get; } = new();

    private string _status = "正在等待采集...";
    private string _topProcess = "启动最多：暂无数据";
    private string _longestProcess = "运行最久：暂无数据";
    private string _currentWindow = "-";
    private string _currentProfile = "保存：间隔 60 秒";

    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }

    public string TopProcess
    {
        get => _topProcess;
        set { if (_topProcess == value) return; _topProcess = value; OnPropertyChanged(); }
    }

    public string LongestProcess
    {
        get => _longestProcess;
        set { if (_longestProcess == value) return; _longestProcess = value; OnPropertyChanged(); }
    }

    public string CurrentWindow
    {
        get => _currentWindow;
        set { if (_currentWindow == value) return; _currentWindow = value; OnPropertyChanged(); }
    }

    public string CurrentProfile
    {
        get => _currentProfile;
        set { if (_currentProfile == value) return; _currentProfile = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
