using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;

namespace CraftStats;

public sealed class CraftStatsService : IDisposable
{
    private readonly AppState _state;
    private readonly SessionTracker _tracker = new();
    private readonly InputHookMonitor _inputMonitor = new();
    private readonly SystemActivityMonitor _activityMonitor;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _saveLock = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _processTimer;
    private readonly DispatcherTimer _windowTimer;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _flushTimer;
    private readonly ConcurrentDictionary<string, long> _pendingCounters = new(StringComparer.OrdinalIgnoreCase);
    private bool _isSuspended;
    private string _lastWindowTitle = string.Empty;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private int _saving;
    private bool _started;
    private bool _disposed;

    public CraftStatsService(AppState state)
    {
        _state = state;
        _activityMonitor = new SystemActivityMonitor(_state, QueueCounter, UpdateDashboardLabels);
        _inputMonitor.KeyPressed += HandleKeyPressed;
        _inputMonitor.MouseButtonPressed += HandleMouseButtonPressed;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => HandleSaveTimer();
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _processTimer.Tick += (_, _) => _activityMonitor.RefreshAll();
        _windowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _windowTimer.Tick += (_, _) => RefreshWindowTracking();
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionTimer.Tick += (_, _) => RefreshSessionMetrics();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += (_, _) => RefreshIdleState();
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _flushTimer.Tick += (_, _) => FlushPendingCounters();
    }

    public async Task StartAsync(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
        await LoadSnapshotAsync();
        GetOrAddCounter("开机次数").IncrementCount();
        GetOrAddCounter("应用启动次数").IncrementCount();
        _tracker.StartSession(_tracker.TakeContinuationStart());
        UpdateHooksFromSettings();
        _saveTimer.Start();
        _processTimer.Start();
        _windowTimer.Start();
        _sessionTimer.Start();
        _idleTimer.Start();
        _flushTimer.Start();
        _started = true;
        _state.Dashboard.Status = "运行中";
        _activityMonitor.RefreshAll();
        RefreshSessionMetrics();
        UpdateDashboardLabels();
        _ = Task.Run(UpdateStartupSetting);
    }

    public List<DailyUptimePoint> GetDailyUptime(int days) => _tracker.GetDailyUptime(days);

    private async Task LoadSnapshotAsync()
    {
        var snapshot = await _state.Store.LoadAsync(_cts.Token);
        if (snapshot is null) return;
        foreach (var pair in snapshot.Counters)
            GetOrAddCounter(pair.Key).Count = pair.Value;
        foreach (var pair in snapshot.Durations)
            GetOrAddCounter(pair.Key).Duration = pair.Value;
        foreach (var item in snapshot.Processes.Values)
        {
            _state.ProcessMap[item.Name] = new ProcessRuntimeState
            {
                Name = item.Name,
                LaunchCount = item.LaunchCount,
                TotalRunningTime = item.TotalRunningTime,
                LastSeenAt = item.LastSeenAt ?? DateTimeOffset.Now
            };
        }
        _tracker.LoadFromSnapshot(snapshot);
        SettingsMapper.ApplyTo(_state.Settings, snapshot.Settings);
    }

    public void ApplySettings()
    {
        _state.Settings.SaveIntervalSeconds = Math.Clamp(_state.Settings.SaveIntervalSeconds, 5, 3600);
        UpdateHooksFromSettings();
        UpdateStartupSetting();
        UpdateDashboardLabels();
    }

    private void UpdateHooksFromSettings()
    {
        var wantsKeyboard = _state.Settings.CollectKeyboardKeys;
        var wantsMouse = _state.Settings.CollectMouseLeftButton || _state.Settings.CollectMouseRightButton || _state.Settings.CollectMouseMiddleButton;
        _inputMonitor.UpdateHooks(wantsKeyboard, wantsMouse);
    }

    private void HandleSaveTimer()
    {
        if (_disposed) return;
        if (_state.Settings.SaveMode == SaveMode.Realtime)
        {
            _ = SaveSilentlyAsync();
            return;
        }
        if (_state.Settings.SaveMode == SaveMode.Interval && (DateTimeOffset.Now - _lastSave).TotalSeconds >= _state.Settings.SaveIntervalSeconds)
            _ = SaveSilentlyAsync();
    }

    private void RefreshWindowTracking()
    {
        if (_isSuspended) return;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return;
        var title = GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title) || title == _lastWindowTitle) return;
        _lastWindowTitle = title;
        _state.Dashboard.CurrentWindow = _state.Settings.CollectWindowTitles ? title : "已隐藏标题";
        QueueCounter("活跃窗口变化");
    }

    private void RefreshSessionMetrics()
    {
        RecomputeSessionMetricCounters();
        _state.Dashboard.Status = _isSuspended
            ? "已暂停"
            : _inputMonitor.LastInstallFailed
                ? "运行中（键鼠采集未能启动，请尝试重新打开）"
                : "运行中";
        UpdateDashboardLabels();
    }

    private void RecomputeSessionMetricCounters()
    {
        var totalRunningTime = _tracker.GetTotal(DateTimeOffset.Now);
        GetOrAddCounter("总计开机时长").Duration = totalRunningTime;
        var count = Math.Max(1, GetOrAddCounter("开机次数").Count);
        GetOrAddCounter("平均每次开机时长").Duration = TimeSpan.FromTicks(totalRunningTime.Ticks / count);
        GetOrAddCounter("已使用天数").Count = _tracker.GetUsedDayCount();
    }

    private void RefreshIdleState()
    {
        if (GetIdleMilliseconds() > 300_000)
            QueueCounter("长时间空闲");
    }

    private void HandleKeyPressed(int vkCode)
    {
        if (!_state.Settings.CollectKeyboardKeys) return;
        if (!_state.Settings.CollectAllKeyboardKeys && !IsLetterOrDigit(vkCode)) return;
        QueueCounter("键盘按键总数");
        QueueCounter($"键盘：{GetKeyName(vkCode)}");
    }

    private void HandleMouseButtonPressed(InputHookMonitor.MouseButton button)
    {
        var enabled = button switch
        {
            InputHookMonitor.MouseButton.Left => _state.Settings.CollectMouseLeftButton,
            InputHookMonitor.MouseButton.Right => _state.Settings.CollectMouseRightButton,
            _ => _state.Settings.CollectMouseMiddleButton
        };
        if (!enabled) return;
        QueueCounter("鼠标点击总数");
        QueueCounter(button switch
        {
            InputHookMonitor.MouseButton.Left => "左键点击",
            InputHookMonitor.MouseButton.Right => "右键点击",
            _ => "中键点击"
        });
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_POWERBROADCAST)
        {
            var code = wParam.ToInt32();
            if (code == NativeMethods.PBT_APMSUSPEND)
            {
                _isSuspended = true;
                _activityMonitor.IsSuspended = true;
                _tracker.MarkSuspended();
                if (_state.Settings.SaveMode == SaveMode.OnSuspendOrShutdown)
                    _ = SaveSilentlyAsync();
            }
            else if (code == NativeMethods.PBT_APMRESUMESUSPEND)
            {
                _isSuspended = false;
                _activityMonitor.IsSuspended = false;
                _tracker.StartSession(DateTimeOffset.Now);
                QueueCounter("休眠次数");
            }
        }
        return 0;
    }

    private void UpdateStartupSetting()
        => StartWithWindowsService.Apply(_state.Settings.StartWithWindows, _state.Settings.StartMinimizedToTray);

    public Task SaveNowAsync() => SaveAsync();

    public async Task ImportAsync(CraftStatsSnapshot snapshot)
    {
        ApplyImportedSnapshot(snapshot);
        _tracker.StartSession(DateTimeOffset.Now);
        UpdateHooksFromSettings();
        UpdateDashboardLabels();
        await SaveAsync();
    }

    public async Task ResetStatsAsync()
    {
        _pendingCounters.Clear();
        _activityMonitor.ResetState();
        _state.CounterMap.Clear();
        _state.ProcessMap.Clear();
        System.Windows.Application.Current.Dispatcher.Invoke(_state.Stats.Clear);
        _tracker.Reset();
        _lastWindowTitle = string.Empty;
        _state.Dashboard.CurrentWindow = "-";
        _state.Dashboard.TopProcess = "启动最多：暂无数据";
        _state.Dashboard.LongestProcess = "运行最久：暂无数据";
        _state.Dashboard.Status = "统计已归零";
        await SaveAsync();
    }

    /// <summary>Waits for any in-flight save and then saves — never drops. Used by user actions, import/reset and shutdown.</summary>
    private async Task SaveAsync()
    {
        while (Interlocked.Exchange(ref _saving, 1) != 0)
            await Task.Delay(20);
        try
        {
            await SaveCoreAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _saving, 0);
        }
    }

    /// <summary>Skips when a save is already running (the next timer tick retries) and logs failures instead of surfacing them.</summary>
    private async Task SaveSilentlyAsync()
    {
        if (Interlocked.Exchange(ref _saving, 1) != 0)
            return;
        try
        {
            await SaveCoreAsync();
        }
        catch (Exception ex)
        {
            AppendCrashLog(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _saving, 0);
        }
    }

    private async Task SaveCoreAsync()
    {
        FlushPendingCounters();
        RecomputeSessionMetricCounters();
        lock (_saveLock)
            _lastSave = DateTimeOffset.Now;

        _state.Snapshot.CreatedAt = DateTimeOffset.Now;
        _state.Snapshot.Counters = _state.CounterMap.Values.ToDictionary(v => v.Name, v => v.Count, StringComparer.OrdinalIgnoreCase);
        _state.Snapshot.Durations = _state.CounterMap.Values.Where(v => v.Duration > TimeSpan.Zero).ToDictionary(v => v.Name, v => v.Duration, StringComparer.OrdinalIgnoreCase);
        _state.Snapshot.Processes = _state.ProcessMap.Values.ToDictionary(v => v.Name, v => new ProcessStat
        {
            Name = v.Name,
            LaunchCount = v.LaunchCount,
            TotalRunningTime = v.TotalRunningTime,
            LastSeenAt = v.LastSeenAt
        }, StringComparer.OrdinalIgnoreCase);
        _state.Snapshot.Sessions = _tracker.CopySessions();
        _state.Snapshot.Settings = SettingsMapper.ToDictionary(_state.Settings);
        await _state.Store.SaveAsync(_state.Snapshot);
    }

    private void ApplyImportedSnapshot(CraftStatsSnapshot snapshot)
    {
        _pendingCounters.Clear();
        _activityMonitor.ResetState();
        _state.CounterMap.Clear();
        _state.ProcessMap.Clear();
        System.Windows.Application.Current.Dispatcher.Invoke(_state.Stats.Clear);

        foreach (var pair in snapshot.Counters)
            GetOrAddCounter(pair.Key).Count = pair.Value;
        foreach (var pair in snapshot.Durations)
            GetOrAddCounter(pair.Key).Duration = pair.Value;
        foreach (var item in snapshot.Processes.Values)
        {
            _state.ProcessMap[item.Name] = new ProcessRuntimeState
            {
                Name = item.Name,
                LaunchCount = item.LaunchCount,
                TotalRunningTime = item.TotalRunningTime,
                LastSeenAt = item.LastSeenAt ?? DateTimeOffset.Now
            };
        }
        _tracker.ReplaceFromSnapshot(snapshot);
        SettingsMapper.ApplyTo(_state.Settings, snapshot.Settings);
    }

    private void UpdateDashboardLabels()
    {
        _state.Dashboard.CurrentProfile = _state.Settings.SaveMode switch
        {
            SaveMode.Realtime => "保存：实时",
            SaveMode.Interval => $"保存：间隔 {_state.Settings.SaveIntervalSeconds} 秒",
            SaveMode.OnSuspendOrShutdown => "保存：休眠/关机时",
            _ => "保存：未知"
        };

        var (topName, topCount) = _activityMonitor.GetTopProcess();
        _state.Dashboard.TopProcess = topCount <= 0 || topName == "-"
            ? "启动最多：暂无数据"
            : $"启动最多：{topCount:N0} 次 · {topName}";
        _state.Dashboard.LongestProcess = _activityMonitor.GetLongestRunningProcess();
    }

    private static bool IsLetterOrDigit(int vkCode)
        => vkCode is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A;

    private static string GetKeyName(int vkCode)
    {
        if (vkCode is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)vkCode).ToString();
        if (vkCode is >= 0x70 and <= 0x87)
            return $"F{vkCode - 0x6F}";

        return vkCode switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 or 0xA0 or 0xA1 => "Shift",
            0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x12 or 0xA4 or 0xA5 => "Alt",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x60 and <= 0x69 => $"Num{vkCode - 0x60}",
            0x6A => "Num*",
            0x6B => "Num+",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => KeyInterop.KeyFromVirtualKey(vkCode).ToString()
        };
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static uint GetIdleMilliseconds()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        return NativeMethods.GetLastInputInfo(ref info) ? NativeMethods.GetTickCount() - info.dwTime : 0;
    }

    private StatEntry GetOrAddCounter(string name) => _state.GetOrCreateCounter(name);

    private void QueueCounter(string name, long delta = 1)
        => _pendingCounters.AddOrUpdate(name, delta, (_, value) => value + delta);

    private void FlushPendingCounters()
    {
        foreach (var pair in _pendingCounters.ToArray())
        {
            if (!_pendingCounters.TryRemove(pair.Key, out var value) || value == 0)
                continue;
            GetOrAddCounter(pair.Key).IncrementCount(value);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        _processTimer.Stop();
        _windowTimer.Stop();
        _sessionTimer.Stop();
        _idleTimer.Stop();
        _flushTimer.Stop();
        FlushPendingCounters();
        _inputMonitor.Dispose();
        _tracker.MarkSuspended();
        if (!_started) return; // startup never finished: the in-memory state is incomplete and saving would wipe the stored snapshot
        try
        {
            // run off the UI thread so the wait-loop and file awaits cannot deadlock against the blocked dispatcher
            Task.Run(SaveAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppendCrashLog(ex);
        }
        _cts.Cancel();
        _cts.Dispose();
    }

    private static void AppendCrashLog(Exception exception)
        => File.AppendAllText(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CraftStats", "crash.log"),
            exception + Environment.NewLine);
}
