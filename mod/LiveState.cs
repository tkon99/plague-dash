using System.Collections.Generic;

namespace PlagueDash
{
    /// <summary>A completed run, archived when a new game starts, so the dashboard
    /// can keep showing it (and a session history of past runs) after the run ends.</summary>
    internal sealed class RunArchive
    {
        public string metaJson;
        public List<DiseaseSnapshot> samples;
        public List<Purchase> purchases;
        public CountrySample[] countries;
        public int countryDay;
        public TechEntry[] techs;
        public int finalDay;
        public string plagueType;   // convenience for the run-selector UI
        public string difficulty;
    }

    /// <summary>
    /// Thread-safe in-memory store of the live run. Replaces the disk-based JSONL
    /// pipeline: the GameUpdate patch publishes here, and the SSE server streams
    /// from here. No files are written.
    ///
    /// History is a bounded ring (oldest samples drop past MAX_HISTORY) so a very
    /// long run can't grow memory without bound. Charts downsample to ~600 points
    /// anyway, so losing samples older than MAX_HISTORY days is fine.
    /// </summary>
    internal static class LiveState
    {
        // Keep at most this many day-samples (~5500 in-game days at normal speed).
        public const int MaxHistory = 6000;
        // Cap the purchase feed (evolves/devolves) to bound memory on long runs.
        public const int MaxPurchases = 500;
        // Keep at most this many completed runs in the session history.
        public const int MaxArchives = 5;

        private static readonly object _lock = new object();
        private static readonly List<DiseaseSnapshot> _history = new List<DiseaseSnapshot>(1024);
        private static readonly List<Purchase> _purchases = new List<Purchase>(128);
        private static readonly List<RunArchive> _archives = new List<RunArchive>(MaxArchives);

        // Latest per-country snapshot (large; keep only the most recent, not the history).
        private static int _lastCountrySnapshotDay = -1;
        private static CountrySample[] _countries;

        // Latest Trait Planner list (replaced whenever techs change).
        private static TechEntry[] _techs;

        // Our own cumulative DNA spend (monotonic). The game's evoPointsSpent is a
        // NET field (it decreases on devolve), so we track a true running total:
        // each evolve adds its cost; devolves don't reduce it (a refund isn't
        // un-spending). Reset per run.
        private static double _cumulativeDnaSpent;

        // Run metadata.
        private static string _metaJson = "{\"status\":\"in_progress\"}";

        // Bumped on every change so the SSE writer can cheaply detect new data.
        private static long _version;

        /// <summary>Append a day sample (deduped by day). Bumps the version counter.</summary>
        public static void Publish(DiseaseSnapshot s)
        {
            if (s == null) return;
            lock (_lock)
            {
                if (_history.Count > 0 && _history[_history.Count - 1].day == s.day) return;
                _history.Add(s);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
                _version++;
            }
        }

        /// <summary>Store the latest per-country breakdown (replaces the previous one).</summary>
        public static void SetCountries(int day, CountrySample[] countries)
        {
            if (countries == null) return;
            lock (_lock)
            {
                _lastCountrySnapshotDay = day;
                _countries = countries;
                _version++;
            }
        }

        /// <summary>Store the latest Trait Planner list (replaces the previous one).</summary>
        public static void SetTechs(TechEntry[] techs)
        {
            lock (_lock) { _techs = techs; _version++; }
        }

        /// <summary>Set the run metadata (a complete meta.json-formatted string).</summary>
        public static void SetMeta(string metaJson)
        {
            lock (_lock) { _metaJson = metaJson ?? _metaJson; _version++; }
        }

        /// <summary>Append an evolve/devolve event to the purchase feed (bounded).</summary>
        public static void PublishPurchase(Purchase p)
        {
            if (p == null) return;
            lock (_lock)
            {
                _purchases.Add(p);
                if (_purchases.Count > MaxPurchases) _purchases.RemoveAt(0);
                _version++;
            }
        }

        /// <summary>Add to our monotonic cumulative DNA spend (called on each evolve).
        /// Devolves don't reduce it — a refund isn't un-spending.</summary>
        public static void AddSpend(double amount)
        {
            if (amount <= 0) return;
            lock (_lock) { _cumulativeDnaSpent += amount; _version++; }
        }

        /// <summary>The monotonic cumulative DNA spent this run.</summary>
        public static double CumulativeDnaSpent
        {
            get { lock (_lock) return _cumulativeDnaSpent; }
        }

        /// <summary>Reset everything for a new run.</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _history.Clear();
                _purchases.Clear();
                _countries = null;
                _lastCountrySnapshotDay = -1;
                _cumulativeDnaSpent = 0;
                _metaJson = "{\"status\":\"in_progress\"}";
                _version++;
            }
        }

        /// <summary>The current version counter; changes when any data changes.</summary>
        public static long Version
        {
            get { lock (_lock) return _version; }
        }

        /// <summary>
        /// Take a consistent snapshot for SSE replay. Returns the meta JSON, a copy
        /// of the history list, the latest country array + its day, and a copy of
        /// the purchase feed.
        /// </summary>
        public static (string metaJson, List<DiseaseSnapshot> history, CountrySample[] countries, int countryDay, List<Purchase> purchases)
            Snapshot()
        {
            lock (_lock)
            {
                var histCopy = new List<DiseaseSnapshot>(_history.Count);
                foreach (var s in _history) histCopy.Add(s);
                var purCopy = new List<Purchase>(_purchases.Count);
                foreach (var p in _purchases) purCopy.Add(p);
                return (_metaJson, histCopy, _countries, _lastCountrySnapshotDay, purCopy);
            }
        }

        /// <summary>The latest Trait Planner list (or null).</summary>
        public static TechEntry[] Techs
        {
            get { lock (_lock) return _techs; }
        }

        // ---- run history ----

        /// <summary>Snapshot the current run into the session history (returns the
        /// archive, or null if there was nothing to archive). Does NOT clear the live
        /// state — callers Reset() separately after archiving.</summary>
        public static RunArchive ArchiveCurrent()
        {
            lock (_lock)
            {
                if (_history.Count == 0 && _purchases.Count == 0) return null;
                var a = new RunArchive
                {
                    metaJson = _metaJson,
                    samples = new List<DiseaseSnapshot>(_history),
                    purchases = new List<Purchase>(_purchases),
                    countries = _countries,
                    countryDay = _lastCountrySnapshotDay,
                    techs = _techs,
                    finalDay = _history.Count > 0 ? _history[_history.Count - 1].day : 0,
                    plagueType = ParseMetaField(_metaJson, "plagueType"),
                    difficulty = ParseMetaField(_metaJson, "difficulty"),
                };
                _archives.Add(a);
                if (_archives.Count > MaxArchives) _archives.RemoveAt(0);
                return a;
            }
        }

        /// <summary>The archived runs, oldest first (copies).</summary>
        public static List<RunArchive> Archives()
        {
            lock (_lock) return new List<RunArchive>(_archives);
        }

        /// <summary>The most recently archived run, or null.</summary>
        public static RunArchive LatestArchive()
        {
            lock (_lock) return _archives.Count > 0 ? _archives[_archives.Count - 1] : null;
        }

        private static string ParseMetaField(string metaJson, string field)
        {
            try
            {
                int i = metaJson.IndexOf("\"" + field + "\"");
                if (i < 0) return "";
                int c = metaJson.IndexOf(':', i);
                if (c < 0) return "";
                int q1 = metaJson.IndexOf('"', c);
                int q2 = metaJson.IndexOf('"', q1 + 1);
                return q1 >= 0 && q2 > q1 ? metaJson.Substring(q1 + 1, q2 - q1 - 1) : "";
            }
            catch { return ""; }
        }
    }
}
