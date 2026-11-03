using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace Overview;

/// <summary>
/// Learns what you use so search can put it first. Two kinds of memory, saved to usage.json:
/// <list type="bullet">
/// <item><b>What you picked for what you typed:</b> open Command Prompt after typing "com" and the
/// prefixes "c", "co" and "com" remember it, so next time "c" alone puts it first. Among several
/// picks for the same letters, the most recent wins.</item>
/// <item><b>What you use in general:</b> every app you switch to or start (from anywhere, not just
/// Overview), weighted by how often and how recently.</item>
/// </list>
/// How well a result matches the text still matters most; this only decides between similar matches.
/// UI thread only.
/// </summary>
internal static class Usage
{
    sealed class Entry
    {
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    sealed class Data
    {
        public Dictionary<string, Entry> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, Entry>> Queries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    const int MaxPrefix = 12;        // longer typed text is remembered by its first 12 letters
    const int MaxPerQuery = 8;       // apps remembered per typed text
    const int MaxApps = 400;
    static readonly TimeSpan Forget = TimeSpan.FromDays(120);
    static readonly TimeSpan HalfLife = TimeSpan.FromDays(3); // recency halves every 3 days
    static readonly TimeSpan CountGap = TimeSpan.FromMinutes(10); // switching back and forth counts once

    static Data _data = new();
    static DispatcherTimer? _saveTimer;

    static string FilePath => Path.Combine(Settings.DataDir, "usage.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                if (d != null)
                {
                    // JSON loses the case-insensitive comparers: rebuild with them.
                    _data = new Data
                    {
                        Apps = new Dictionary<string, Entry>(d.Apps ?? new(), StringComparer.OrdinalIgnoreCase),
                        Queries = (d.Queries ?? new()).ToDictionary(
                            kv => kv.Key,
                            kv => new Dictionary<string, Entry>(kv.Value ?? new(), StringComparer.OrdinalIgnoreCase),
                            StringComparer.OrdinalIgnoreCase)
                    };
                }
            }
        }
        catch (Exception e) { Log.Write(e); }
        Prune();
    }

    public static void Save()
    {
        _saveTimer?.Stop();
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data));
        }
        catch (Exception e) { Log.Write(e); }
    }

    /// <summary>Saves a few seconds after the last change, so bursts of activity write once.</summary>
    static void SaveSoon()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _saveTimer.Tick += (_, _) => Save();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    static string Normalize(string query) =>
        string.Join(' ', query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static void Bump(Dictionary<string, Entry> map, string key, bool alwaysCount)
    {
        var now = DateTime.UtcNow;
        if (!map.TryGetValue(key, out var e)) map[key] = e = new Entry();
        if (alwaysCount || now - e.Last > CountGap) e.Count++;
        e.Last = now;
    }

    /// <summary>Off in Settings → nothing is learned and results aren't reordered.</summary>
    static bool Enabled => App.Settings.LearnFromUsage;

    /// <summary>Forgets everything search has learned (Settings → Clear history).</summary>
    public static void Clear()
    {
        _data = new Data();
        Save();
    }

    /// <summary>You picked this app (or its window) from search, after typing <paramref name="query"/>.</summary>
    public static void RecordPick(string key, string query)
    {
        if (!Enabled) return;
        Bump(_data.Apps, key, alwaysCount: true);
        string q = Normalize(query);
        for (int len = 1; len <= Math.Min(q.Length, MaxPrefix); len++)
        {
            string prefix = q.Substring(0, len);
            if (!_data.Queries.TryGetValue(prefix, out var picks))
                _data.Queries[prefix] = picks = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            Bump(picks, key, alwaysCount: true);
            if (picks.Count > MaxPerQuery)
                picks.Remove(picks.OrderBy(kv => kv.Value.Last).First().Key);
        }
        SaveSoon();
    }

    /// <summary>A window of this app came to the front (from anywhere).</summary>
    public static void RecordUse(string key)
    {
        if (!Enabled) return;
        Bump(_data.Apps, key, alwaysCount: false);
        SaveSoon();
    }

    /// <summary>1 = just now, 0.5 = three days ago, 0.25 = six days ago, …</summary>
    static double Recency(DateTime last) =>
        Math.Pow(0.5, Math.Max(0, (DateTime.UtcNow - last).TotalHours) / HalfLife.TotalHours);

    /// <summary>
    /// Points added to a search result's match score. A match score is up to 100 (exact name ≈ 100,
    /// name starts with the text ≈ 90, a later word starts with it ≈ 70).
    /// </summary>
    public static double Bonus(string? key, string query)
    {
        if (key == null || !Enabled) return 0;
        double bonus = 0;
        if (_data.Apps.TryGetValue(key, out var used))
            bonus += Math.Min(15, 5 * Math.Log2(1 + used.Count)) * (0.3 + 0.7 * Recency(used.Last));

        string q = Normalize(query);
        if (q.Length > MaxPrefix) q = q.Substring(0, MaxPrefix);
        if (q.Length > 0 && _data.Queries.TryGetValue(q, out var picks) && picks.TryGetValue(key, out var pick))
        {
            bonus += 30 + 30 * Recency(pick.Last) + Math.Min(8, 2 * Math.Log2(1 + pick.Count));
            // The latest pick for these letters wins, even if another one was also picked recently.
            int newer = picks.Values.Count(p => p.Last > pick.Last);
            bonus += newer == 0 ? 12 : newer == 1 ? 5 : 0;
        }
        return bonus;
    }

    static void Prune()
    {
        var cutoff = DateTime.UtcNow - Forget;
        foreach (var k in _data.Apps.Where(kv => kv.Value.Last < cutoff).Select(kv => kv.Key).ToList()) _data.Apps.Remove(k);
        if (_data.Apps.Count > MaxApps)
            foreach (var k in _data.Apps.OrderBy(kv => kv.Value.Last).Take(_data.Apps.Count - MaxApps).Select(kv => kv.Key).ToList())
                _data.Apps.Remove(k);
        foreach (var (q, picks) in _data.Queries.ToList())
        {
            foreach (var k in picks.Where(kv => kv.Value.Last < cutoff).Select(kv => kv.Key).ToList()) picks.Remove(k);
            if (picks.Count == 0) _data.Queries.Remove(q);
        }
    }
}
