using System.Diagnostics;
using System.Net.NetworkInformation;

namespace CraftStats;

/// <summary>Collects foreground process, process inventory, file IO and network deltas.</summary>
public sealed class SystemActivityMonitor
{
    private readonly AppState _state;
    private readonly Action<string, long> _queueCounter;
    private readonly Action _uiRefresh;
    private readonly Dictionary<int, string> _knownProcessIds = new();
    private readonly Dictionary<string, NativeMethods.IO_COUNTERS> _lastIoCounters = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;
    private long _lastNetworkReceived;
    private long _lastNetworkSent;
    private bool _networkPrimed;
    private string _lastForegroundProcess = string.Empty;
    private int _sweepRunning;

    public bool IsSuspended { get; set; }

    public SystemActivityMonitor(AppState state, Action<string, long> queueCounter, Action uiRefresh)
    {
        _state = state;
        _queueCounter = queueCounter;
        _uiRefresh = uiRefresh;
    }

    public void RefreshAll()
    {
        RefreshForegroundProcess();
        RefreshForegroundFileIo();
        RefreshNetworkStats();
        if (Interlocked.Exchange(ref _sweepRunning, 1) == 1)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                RefreshProcessInventory();
            }
            finally
            {
                Interlocked.Exchange(ref _sweepRunning, 0);
            }
        });
    }

    public void ResetState()
    {
        _knownProcessIds.Clear();
        _lastIoCounters.Clear();
        _lastForegroundProcess = string.Empty;
        _lastNetworkReceived = 0;
        _lastNetworkSent = 0;
        _networkPrimed = false;
        _lastSweep = DateTimeOffset.MinValue;
    }

    private void RefreshProcessInventory()
    {
        if (IsSuspended) return;
        var now = DateTimeOffset.Now;
        var delta = _lastSweep == DateTimeOffset.MinValue ? TimeSpan.Zero : now - _lastSweep;
        _lastSweep = now;
        var liveIds = new HashSet<int>();
        // multiple same-name processes share one ProcessRuntimeState; credit the sweep delta
        // only once per distinct process name so running times are not multiplied by instance count
        var creditedThisSweep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
                    var name = _state.Settings.CollectProcessNames ? process.ProcessName : "Process";
                    liveIds.Add(id);
                    var runtime = _state.ProcessMap.GetOrAdd(name, n => new ProcessRuntimeState { Name = n });
                    if (!_knownProcessIds.ContainsKey(id))
                    {
                        _knownProcessIds[id] = name;
                        runtime.LaunchCount++;
                        _queueCounter($"进程启动：{name}", 1);
                    }
                    if (creditedThisSweep.Add(name))
                        runtime.TotalRunningTime += delta;
                    runtime.LastSeenAt = now;
                }
                catch
                {
                }
            }
        }

        foreach (var id in _knownProcessIds.Keys.Where(id => !liveIds.Contains(id)).ToList())
            _knownProcessIds.Remove(id);

        _uiRefresh();
    }

    private void RefreshForegroundProcess()
    {
        if (IsSuspended) return;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = _state.Settings.CollectProcessNames ? process.ProcessName : "Process";
            if (_lastForegroundProcess != name)
            {
                _lastForegroundProcess = name;
                _queueCounter($"进程切换：{name}", 1);
            }
        }
        catch
        {
        }
    }

    private void RefreshForegroundFileIo()
    {
        if (!_state.Settings.CollectFileStats || IsSuspended) return;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = _state.Settings.CollectProcessNames ? process.ProcessName : "Process";
            if (!NativeMethods.GetProcessIoCounters(process.Handle, out var counters))
                return;
            if (_lastIoCounters.TryGetValue(name, out var previous))
            {
                AddPositiveDelta("文件读取字节", ToLongDelta(counters.ReadTransferCount, previous.ReadTransferCount));
                AddPositiveDelta("文件写入字节", ToLongDelta(counters.WriteTransferCount, previous.WriteTransferCount));
                AddPositiveDelta($"文件读取字节：{name}", ToLongDelta(counters.ReadTransferCount, previous.ReadTransferCount));
                AddPositiveDelta($"文件写入字节：{name}", ToLongDelta(counters.WriteTransferCount, previous.WriteTransferCount));
            }
            _lastIoCounters[name] = counters;
        }
        catch
        {
        }
    }

    private void RefreshNetworkStats()
    {
        if (!_state.Settings.CollectNetworkStats) return;
        long received = 0;
        long sent = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            try
            {
                var stats = adapter.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch
            {
            }
        }

        if (_networkPrimed)
        {
            AddPositiveDelta("网络接收字节", received - _lastNetworkReceived);
            AddPositiveDelta("网络发送字节", sent - _lastNetworkSent);
        }
        _lastNetworkReceived = received;
        _lastNetworkSent = sent;
        _networkPrimed = true;
    }

    private void AddPositiveDelta(string name, long delta)
    {
        if (delta > 0)
            _queueCounter(name, delta);
    }

    private static long ToLongDelta(ulong current, ulong previous)
    {
        if (current <= previous) return 0;
        var delta = current - previous;
        return delta > long.MaxValue ? long.MaxValue : (long)delta;
    }

    public (string Name, long Count) GetTopProcess()
    {
        var best = _state.ProcessMap.Values.OrderByDescending(p => p.LaunchCount).FirstOrDefault();
        return best is null ? ("-", 0) : (best.Name, best.LaunchCount);
    }

    public string GetLongestRunningProcess()
    {
        var best = _state.ProcessMap.Values.OrderByDescending(p => p.TotalRunningTime).FirstOrDefault();
        return best is null ? "运行最久：暂无数据" : $"运行最久：{DurationFormat.Format(best.TotalRunningTime)} · {best.Name}";
    }
}
