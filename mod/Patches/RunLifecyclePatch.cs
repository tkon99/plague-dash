using HarmonyLib;

namespace PlagueDash.Patches
{
    /// <summary>
    /// Detects a genuinely NEW game so the dashboard archives the finished run and
    /// starts a fresh one. Hooked on CGameManager.CreateGame — a static method that
    /// only fires when the player actually starts a new game. (Hooking SPDisease.
    /// Initialise was wrong: it also fires during the end-screen replay, which wiped
    /// the dashboard's data.)
    ///
    /// The archive keeps the finished run available in the session history; the live
    /// buffers are then reset for the new run. Run metadata (plague type, difficulty,
    /// origin) is written on the new run's first in-game day by DiseaseStatsPatch,
    /// once the disease + game objects exist.
    /// </summary>
    [HarmonyPatch(typeof(CGameManager), "CreateGame")]
    internal static class RunLifecyclePatch
    {
        private static void Postfix()
        {
            if (!Main.Enabled) return;

            // Archive the previous run (if it had any data), then reset for the new one.
            var archived = LiveState.ArchiveCurrent();
            StatsRecorder.StartRun("", "", "");
            DiseaseStatsPatch.Reset();
            if (archived != null)
                Main.Log($"New game: archived previous run (day {archived.finalDay}, {archived.plagueType}).");
            else
                Main.Log("New game started.");
        }

        private static bool _metaWritten;

        /// <summary>Write run metadata once per run. Called by DiseaseStatsPatch on
        /// the first in-game day, when the disease + game objects are ready.</summary>
        public static void WriteMetaIfNeeded(SPDisease disease)
        {
            if (_metaWritten) return;
            _metaWritten = true;
            try
            {
                string plague = ReadPlagueType(disease);
                string difficulty = ReadDifficulty();
                string origin = ReadOrigin(disease);
                StatsRecorder.StartRun(plague, difficulty, origin);
                Main.Log($"Run meta: {plague} / {difficulty} / {origin}");
            }
            catch (System.Exception e) { Main.Log("WriteMeta failed: " + e.Message); }
        }

        /// <summary>Reset the once-per-run meta guard. Called from DiseaseStatsPatch.Reset().</summary>
        public static void ResetMeta() => _metaWritten = false;

        // --- defensive metadata readers ---
        private static string ReadPlagueType(SPDisease disease)
        {
            try
            {
                return S(disease, "diseaseName") ?? S(disease, "name") ?? "";
            }
            catch { return ""; }
        }

        private static string ReadDifficulty()
        {
            try
            {
                // IGame.Difficulty is a UInt32 index; resolve it to the localised
                // human-readable name ("Normal", "Brutal", ...) via the game's own
                // helper rather than the raw DifficultyNames key.
                object game = AccessTools.Field(typeof(CGameManager), "game").GetValue(null);
                if (game == null) return "";
                uint diffIdx = (uint)Traverse.Create(game).Property("Difficulty").GetValue();
                string loc = AccessTools.Method(typeof(CGameManager), "GetDifficultyLocalisedString",
                                new[] { typeof(int), typeof(bool) })
                            .Invoke(null, new object[] { (int)diffIdx, false }) as string;
                if (!string.IsNullOrEmpty(loc)) return loc;
                return "Difficulty " + diffIdx;
            }
            catch { return ""; }
        }

        private static string ReadOrigin(SPDisease disease)
        {
            try
            {
                // SPDisease.drillZeroCountry is the patient-zero / player-selected
                // start country (a Country object). Read its display name.
                object country = Traverse.Create(disease).Property("drillZeroCountry").GetValue();
                if (country == null) return "";
                string name = Traverse.Create(country).Field("name").GetValue<string>();
                return name ?? "";
            }
            catch { return ""; }
        }

        // Read a string member; try property first (the game's stats are exposed as
        // properties), then fall back to a plain field.
        private static string S(object obj, string name)
        {
            try
            {
                string v = Traverse.Create(obj).Property(name).GetValue<string>();
                if (!string.IsNullOrEmpty(v)) return v;
                return Traverse.Create(obj).Field(name).GetValue<string>();
            }
            catch { return null; }
        }
    }
}
