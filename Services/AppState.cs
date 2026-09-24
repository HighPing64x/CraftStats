using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace CraftStats;

public sealed class AppState
{
    public DashboardState Dashboard { get; } = new();
    public AppSettingsState Settings { get; } = new();
    public CraftStatsSnapshot Snapshot { get; } = new();
    public CraftStatsStore Store { get; } = new();
    public ConcurrentDictionary<string, StatEntry> CounterMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, ProcessRuntimeState> ProcessMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<StatEntry> Stats => Dashboard.Items;

    public StatEntry GetOrCreateCounter(string name)
        => CounterMap.GetOrAdd(name, n =>
        {
            var entry = new StatEntry(n);
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
                Stats.Add(entry);
            else
                dispatcher.BeginInvoke(() => Stats.Add(entry));
            return entry;
        });
}

public sealed class ProcessRuntimeState
{
    public string Name { get; init; } = string.Empty;
    public long LaunchCount { get; set; }
    public TimeSpan TotalRunningTime { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? ActiveSince { get; set; }
}
