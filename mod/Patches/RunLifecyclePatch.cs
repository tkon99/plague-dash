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
                // SPDisease.drillZeroCountry is set-only ({set;}), so reading it as
                // a property returns null. The protected backing field on Disease
                // (_drillZeroCountry) holds the actual Country reference.
                object country = AccessTools.Field(typeof(Disease), "_drillZeroCountry").GetValue(disease);
                if (country == null)
                {
                    // Fallback: try the property in case a build exposes a getter.
                    country = Traverse.Create(disease).Property("drillZeroCountry").GetValue();
                }
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

    /// <summary>
    /// Authoritative run-end detection. CInterfaceManager.EndGame is raised once
    /// at the exact moment the end-screen appears, for BOTH outcomes (extinction
    /// win and cure-complete loss). Its parameters carry the full result — unlike
    /// polling IGame.CurrentGameState (which doesn't reliably reach EndGame on
    /// extinction) or the nonexistent IGame.gameWon field.
    ///
    /// Verified signature against Assembly-CSharp.dll:
    ///   void EndGame(IGame.GameType gameType, bool didWin, Disease diseaseWon,
    ///                IGame.EndGameResult resultType, CUIManager.Unlock unlocked)
    /// EndGameResult (Int32): Dead=2 (extinction win), Cured=3 (cure loss).
    /// </summary>
    [HarmonyPatch(typeof(CInterfaceManager), "EndGame")]
    internal static class EndGamePatch
    {
        private static bool _fired;  // guard: EndGame can fire more than once; act once

        private static void Postfix(bool didWin, object resultType)
        {
            if (!Main.Enabled) return;
            if (_fired) return;
            _fired = true;
            try
            {
                // resultType is IGame.EndGameResult boxed as object; read its int.
                // Dead=2 means everyone died (plague wins); Cured=3 means the cure
                // completed (plague loses).
                int result = 0;
                if (resultType != null) result = System.Convert.ToInt32(resultType);
                bool extinctionWin = didWin && result == 2;
                bool cureLoss = !didWin && result == 3;
                Main.Log($"EndGame: didWin={didWin} result={result}" +
                         (extinctionWin ? " (extinction win)" : cureLoss ? " (cure loss)" : ""));
                StatsRecorder.EndRun(didWin);
            }
            catch (System.Exception e) { Main.Log("EndGame postfix failed: " + e.Message); }
        }

        /// <summary>Reset the once-per-run guard. Called from DiseaseStatsPatch.Reset().</summary>
        public static void ResetEndGame() => _fired = false;
    }
}
