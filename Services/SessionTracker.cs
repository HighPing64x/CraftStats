namespace CraftStats;

/// <summary>Tracks power-on sessions, derives totals and per-day uptime, and repairs sessions cut short by process death.</summary>
public sealed class SessionTracker
{
    private const int SessionRetentionDays = 365;
    private static readonly TimeSpan BootTimeTolerance = TimeSpan.FromSeconds(60);

    private readonly List<SessionRecord> _sessions = new();
    private TimeSpan _baseline = TimeSpan.Zero;
    private DateTimeOffset? _continuationStart;

    public IReadOnlyList<SessionRecord> Sessions => _sessions;

    public void StartSession(DateTimeOffset startedAt)
    {
        // guard: a duplicate resume event must not leave two open (overlapping, double-counted) sessions
        EndOpenSession(startedAt);
        _sessions.Add(new SessionRecord { StartedAt = startedAt });
    }

    public void MarkSuspended() => EndOpenSession(DateTimeOffset.Now);

    public TimeSpan GetTotal(DateTimeOffset openSessionEnd)
        => _baseline + SumDurations(_sessions, openSessionEnd);

    public List<DailyUptimePoint> GetDailyUptime(int days)
    {
        days = Math.Clamp(days, 1, 366);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var oldest = today.AddDays(-(days - 1));
        var sums = new TimeSpan[days];
        var now = DateTimeOffset.Now;

        // merge overlapping sessions first so the same wall-clock time is never
        // credited twice (a single day can therefore never exceed 24h)
        foreach (var (startOffset, endOffset) in MergedIntervals(_sessions, now))
        {
            var start = startOffset.LocalDateTime;
            var end = endOffset.LocalDateTime;
            if (end <= start) continue;

            var current = start;
            while (current < end)
            {
                var nextMidnight = current.Date.AddDays(1);
                var segmentEnd = end < nextMidnight ? end : nextMidnight;
                var index = DateOnly.FromDateTime(current.Date).DayNumber - oldest.DayNumber;
                if ((uint)index < (uint)days)
                    sums[index] += segmentEnd - current;
                current = segmentEnd;
            }
        }

        var result = new List<DailyUptimePoint>(days);
        for (var i = 0; i < days; i++)
            result.Add(new DailyUptimePoint(oldest.AddDays(i), sums[i]));
        return result;
    }

    /// <summary>Number of distinct calendar days on which the software recorded any activity.</summary>
    public int GetUsedDayCount()
    {
        var dates = new HashSet<DateOnly>();
        foreach (var (startOffset, endOffset) in MergedIntervals(_sessions, DateTimeOffset.Now))
        {
            var current = DateOnly.FromDateTime(startOffset.LocalDateTime);
            var last = DateOnly.FromDateTime(endOffset.LocalDateTime);
            while (current <= last)
            {
                dates.Add(current);
                current = current.AddDays(1);
            }
        }
        return dates.Count;
    }

    public void LoadFromSnapshot(CraftStatsSnapshot snapshot)
    {
        _continuationStart = ResolveContinuationStart(snapshot);
        NormalizeSnapshot(snapshot);
        _sessions.AddRange(snapshot.Sessions);
        _baseline = CalculateBaseline(snapshot);
    }

    public void ReplaceFromSnapshot(CraftStatsSnapshot snapshot)
    {
        _sessions.Clear();
        LoadFromSnapshot(snapshot);
    }

    /// <summary>Returns where the new session should begin: the crash-repair point, or now.</summary>
    public DateTimeOffset TakeContinuationStart()
    {
        var start = _continuationStart ?? DateTimeOffset.Now;
        _continuationStart = null;
        return start;
    }

    public List<SessionRecord> CopySessions() => _sessions.ToList();

    public void Reset()
    {
        _sessions.Clear();
        _baseline = TimeSpan.Zero;
        _continuationStart = null;
        StartSession(DateTimeOffset.Now);
    }

    private void EndOpenSession(DateTimeOffset endedAt)
    {
        if (_sessions.Count > 0 && _sessions[^1].EndedAt is null)
            _sessions[^1].EndedAt = endedAt;
    }

    // A saved snapshot whose last session is still open means the process died unexpectedly
    // (crash / force kill / power loss): clean exits and suspend saves close it first.
    // When that death clearly happened inside the current power-on period (per the kernel
    // boot time), resume counting from the last save so the gap is not lost. Tails from
    // earlier power-on periods are unrecoverable (the machine may have been off) and stay cut.
    private static DateTimeOffset? ResolveContinuationStart(CraftStatsSnapshot snapshot)
    {
        if (snapshot.Sessions.Count == 0) return null;
        if (snapshot.Sessions[^1].EndedAt is not null) return null;

        var now = DateTimeOffset.Now;
        var lastKnown = snapshot.CreatedAt == default ? now : snapshot.CreatedAt;
        var bootTime = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (lastKnown <= bootTime + BootTimeTolerance || lastKnown >= now) return null;
        return lastKnown;
    }

    private static void NormalizeSnapshot(CraftStatsSnapshot snapshot)
    {
        var savedAt = snapshot.CreatedAt == default ? DateTimeOffset.Now : snapshot.CreatedAt;
        foreach (var session in snapshot.Sessions)
        {
            if (session.EndedAt is not null) continue;
            session.EndedAt = savedAt > session.StartedAt ? savedAt : session.StartedAt;
        }

        // keep the snapshot bounded; totals stay correct because the trimmed time is
        // folded into the baseline by CalculateBaseline
        var cutoff = DateTimeOffset.Now.AddDays(-SessionRetentionDays);
        snapshot.Sessions.RemoveAll(session => session.StartedAt < cutoff && (session.EndedAt ?? savedAt) < cutoff);
    }

    private static TimeSpan CalculateBaseline(CraftStatsSnapshot snapshot)
    {
        var savedTotal = TimeSpan.Zero;
        if (snapshot.Durations.TryGetValue("总计开机时长", out var duration))
            savedTotal = duration;
        else if (snapshot.Counters.TryGetValue("总计开机时长", out var counterValue))
            savedTotal = TimeSpan.FromTicks(counterValue);

        var savedAt = snapshot.CreatedAt == default ? DateTimeOffset.Now : snapshot.CreatedAt;
        var sessionTotal = SumDurations(snapshot.Sessions, savedAt);
        return savedTotal > sessionTotal ? savedTotal - sessionTotal : TimeSpan.Zero;
    }

    private static TimeSpan SumDurations(IEnumerable<SessionRecord> sessions, DateTimeOffset openSessionEnd)
    {
        var total = TimeSpan.Zero;
        foreach (var (start, end) in MergedIntervals(sessions, openSessionEnd))
            total += end - start;
        return total;
    }

    /// <summary>Projects sessions onto the union of their time ranges, so overlapping
    /// (double-counted) sessions only contribute once. Intervals are contiguous but not merged
    /// when they merely touch without overlap.</summary>
    private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> MergedIntervals(
        IEnumerable<SessionRecord> sessions, DateTimeOffset openSessionEnd)
    {
        var raw = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var session in sessions)
        {
            if (session.StartedAt == default) continue;
            var end = session.EndedAt ?? openSessionEnd;
            if (end <= session.StartedAt) continue;
            raw.Add((session.StartedAt, end));
        }
        raw.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var (start, end) in raw)
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                if (end > merged[^1].End)
                    merged[^1] = (merged[^1].Start, end);
            }
            else
            {
                merged.Add((start, end));
            }
        }
        return merged;
    }
}
