using HarmonyLib;

namespace PlagueDash.Patches
{
    /// <summary>
    /// Hooks SPDisease.GameUpdate() — the game's per-tick disease update. After it
    /// runs, we read the live field values off the disease instance, build a
    /// snapshot, and append one line to plague-dash.jsonl when the in-game day
    /// advances. This is the primary data-collection patch.
    ///
    /// Field access uses Traverse (Harmony reflection helper) with fallbacks so a
    /// renamed or private field degrades gracefully instead of throwing.
    /// </summary>
    [HarmonyPatch(typeof(SPDisease), "GameUpdate")]
    internal static class DiseaseStatsPatch
    {
        private static int _lastDay = -1;
        private static bool _runEnded;          // true once the run has finished
        // High-water mark of cure progress. The game's cureCompletePercent can
        // DROP sharply — e.g. from 99% to 1% when a near-complete cure "fails"
        // against a globally-established plague and effort resets. That snap
        // disrupts the race chart/gauge (looks like a bug). For display we keep
        // cure monotonic: once it has reached a peak it never visibly regresses.
        private static double _curePeak;

        /// <summary>The most recently computed in-game day, for other patches (e.g.
        /// DNA-purchase events need a day but don't compute one themselves).</summary>
        public static int CurrentDay => _dayCounter > 0 ? _dayCounter : (_lastDay > 0 ? _lastDay : 0);

        /// <summary>Reset the per-run day throttle + day counter.
        /// Called when a new run starts.</summary>
        internal static void Reset()
        {
            _lastDay = -1;
            _dayCounter = 0;
            _lastDate = default(System.DateTime);
            _tickFallback = 0;
            _countrySamples = null;
            _runEnded = false;
            _curePeak = 0;
            RunLifecyclePatch.ResetMeta();
            EndGamePatch.ResetEndGame();
        }

        /// <summary>True when the current game has reached its end state, so we
        /// should freeze recording (stop letting post-run menu zeros pollute the
        /// charts). Reads IGame.CurrentGameState via CGameManager.game:
        ///   None=0, Initialise=1, Choosing=2, ChoosingCountry=3, InProgress=4, EndGame=5
        /// ONLY EndGame (5) counts. The actual win/lose outcome is determined
        /// authoritatively by the RunLifecyclePatch.EndGamePatch hook on
        /// CInterfaceManager.EndGame (which carries didWin + resultType as params);
        /// this method no longer tries to infer it.</summary>
        private static bool TryGetRunEnded(SPDisease disease, out bool won)
        {
            won = false;
            try
            {
                object game = AccessTools.Field(typeof(CGameManager), "game").GetValue(null);
                if (game == null) return false;
                object state = Traverse.Create(game).Property("CurrentGameState").GetValue();
                if (state == null) return false;
                int st = System.Convert.ToInt32(state);
                if (st != 5) return false; // only EndGame counts
                // 'won' here is just a freeze flag placeholder; the real outcome
                // is set by EndGamePatch. Default to the last-known meta status.
                return true;
            }
            catch { return false; }
        }

        private static void Postfix(SPDisease __instance)
        {
            if (!Main.Enabled || !Main.Settings.Recording) return;

            // Single-player-only guard: never collect data in a multiplayer game.
            // Using Plague Dash in MP gives a competitive advantage and risks a ban.
            if (MultiplayerGuard.IsMultiplayer) return;

            // Run-end freeze: once the game is no longer InProgress (won/lost), stop
            // capturing so the post-run menu state (zeros) doesn't pollute the charts.
            // Finalize the meta status once.
            if (TryGetRunEnded(__instance, out bool won))
            {
                if (!_runEnded)
                {
                    _runEnded = true;
                    StatsRecorder.EndRun(won);
                    Main.Log("Run ended: " + (won ? "won" : "lost") + " — dashboard frozen at day " + CurrentDay);
                }
                return;
            }

            int day = ReadDay(__instance);

            // Fallback: if we never got a real date (currentGameDate reads null at
            // menu / pre-start), advance a tick-based pseudo-day every ~60 GameUpdate
            // calls so charts still populate. Tuned so ~1 in-game day ≈ 60 ticks.
            if (day <= 0)
            {
                _tickFallback++;
                if (_tickFallback % 60 != 0) return;   // only every 60th tick
                day = _tickFallback / 60;
            }

            // First day of a new run: write the run metadata (plague type, difficulty,
            // origin) now that the disease + game objects exist.
            if (day == 1) RunLifecyclePatch.WriteMetaIfNeeded(__instance);

            if (day == _lastDay) return; // already sampled this day

            var s = new DiseaseSnapshot { day = day };

            // --- population totals (optional, best-effort) ---
            s.infected = F(__instance, "totalInfected") ?? F(__instance, "infectedPopulation");
            s.dead = F(__instance, "totalDead");

            // --- core stats (use the VISUAL values the game's UI shows the player) ---
            // The game smooths/ramps these over turns: the raw globalInfectiousness /
            // globalLethality jump instantly on evolve, but the player only sees
            // reproductionVisual / mortalityVisual which ramp gradually. Showing the
            // raw values would disclose the underlying lethality lag — i.e. let the
            // player see a coming spike before the game's own bar reveals it. We use
            // the same visual values the disease screen's bars use (via
            // Disease.GetInfSevLethTotal → reproductionVisual / mortalityVisual).
            // Severity has no visual ramp; it's read directly.
            s.inf = F(__instance, "reproductionVisual") ?? F(__instance, "globalInfectiousness");
            s.sev = F(__instance, "globalSeverity");
            s.let = F(__instance, "mortalityVisual") ?? F(__instance, "globalLethality");

            // --- cure ---
            s.cureNeed = F(__instance, "cureRequirements");
            double? spd = F(__instance, "globalCureResearchThisTurn");
            double? ineff = F(__instance, "researchInefficiencyMultiplier");
            if (spd.HasValue)
            {
                double m = ineff.HasValue ? (1.0 - ineff.Value) : 1.0;
                s.cureSpd = spd.Value * m;
            }
            // cure percent: the game's own cureCompletePercent (0..1).
            double? curePct = F(__instance, "cureCompletePercent");
            if (curePct.HasValue)
            {
                double cp = curePct.Value > 1.0 ? curePct.Value / 100.0 : curePct.Value;
                // Clamp to the high-water mark so a failed-cure reset (e.g. 99% -> 1%
                // when a near-complete cure fails against an established plague)
                // doesn't make the race chart/gauge snap backwards. The displayed
                // cure progress is monotonic — once reached, effort isn't "lost".
                if (cp > _curePeak) _curePeak = cp;
                s.curePct = _curePeak;
            }

            // --- mutation ---
            s.mutCnt = F(__instance, "mutationCounter");

            // --- DNA economy ---
            // Balance (evoPoints) is reliable. The game's evoPointsSpent is a NET
            // field (it decreases on devolve), so it can't give a monotonic total
            // spent. We track our own running cumulative spend in LiveState
            // (incremented by the EvolveTech patch); read that here.
            s.dna = F(__instance, "evoPoints");
            double cumSpent = LiveState.CumulativeDnaSpent;
            s.dnaSpent = cumSpent;
            s.dnaEarned = cumSpent + (s.dna ?? 0);

            // --- transmission effectiveness ---
            s.airT = F(__instance, "airTransmission");
            s.seaT = F(__instance, "seaTransmission");
            s.landT = F(__instance, "landTransmission");
            s.corpseT = F(__instance, "corpseTransmission");

            // --- per-country / per-continent (granular geographic tracking) ---
            // Enumerate World.instance.countries (List<Country>), cast each to
            // SPCountry (where the infection stats live as properties), and build
            // both a continent rollup (attached to this day's line) and a full
            // per-country snapshot (written every ~25 days to limit file size).
            CollectGeography(day, s);

            _lastDay = day;
            StatsRecorder.RecordSample(s);

            // Full per-country snapshot, throttled (separate file).
            if (_countrySamples != null)
                StatsRecorder.RecordCountries(day, _countrySamples);

            // Trait Planner: re-collect when the day advances (techs only change on
            // evolve, and day changes are rare relative to ticks).
            CollectTechs(__instance);
        }

        private static CountrySample[] _countrySamples;

        /// <summary>Enumerate the disease's technologies and publish the unevolved,
        /// evolvable ones with their current cost + effect deltas for the dashboard's
        /// Trait Planner panel.</summary>
        private static void CollectTechs(SPDisease disease)
        {
            try
            {
                // Disease.technologies is a List<Technology>; techEvolved is a
                // HashSet<string> of evolved tech ids.
                var techList = AccessTools.Field(typeof(Disease), "technologies").GetValue(disease) as System.Collections.IList;
                if (techList == null || techList.Count == 0) return;

                var entries = new System.Collections.Generic.List<TechEntry>(techList.Count);
                foreach (var tech in techList)
                {
                    if (tech == null) continue;
                    var t = Traverse.Create(tech);
                    // Skip already-evolved and padlocked techs.
                    bool preEvolved = t.Property("isPreEvolved").GetValue<bool>();
                    bool padlocked = t.Field("padlocked").GetValue<bool>();
                    if (preEvolved || padlocked) continue;
                    // Only include techs the player can actually evolve right now.
                    bool canEvolve = (bool)AccessTools.Method(typeof(Disease), "CanEvolve", new[] { typeof(Technology) })
                                     .Invoke(disease, new[] { tech });
                    if (!canEvolve) continue;

                    var e = new TechEntry
                    {
                        id = t.Field("id").GetValue<string>(),
                        name = t.Field("name").GetValue<string>(),
                        // category field is empty at runtime; use gridType (ETechType).
                        category = TechCategoryOf(tech),
                        cost = ToD(AccessTools.Method(typeof(SPDisease), "GetEvolveCost", new[] { typeof(Technology) })
                                          .Invoke(disease, new[] { tech })),
                        infectivity = ToD(t.Field("changeToInfectiousness").GetValue()),
                        severity = ToD(t.Field("changeToSeverity").GetValue()),
                        lethality = ToD(t.Field("changeToLethality").GetValue()),
                        air = ToD(t.Field("changeToAirTransmission").GetValue()),
                        sea = ToD(t.Field("changeToSeaTransmission").GetValue()),
                        land = ToD(t.Field("changeToLandTransmission").GetValue()),
                        corpse = ToD(t.Field("changeToCorpseTransmission").GetValue()),
                        wealthy = ToD(t.Field("changeToWealthy").GetValue()),
                        poverty = ToD(t.Field("changeToPoverty").GetValue()),
                        urban = ToD(t.Field("changeToUrban").GetValue()),
                        rural = ToD(t.Field("changeToRural").GetValue()),
                        hot = ToD(t.Field("changeToHot").GetValue()),
                        cold = ToD(t.Field("changeToCold").GetValue()),
                        arid = ToD(t.Field("changeToArid").GetValue()),
                        humid = ToD(t.Field("changeToHumid").GetValue()),
                        cureMulti = ToD(t.Field("changeToCureBaseMultiplier").GetValue()),
                        researchIneff = ToD(t.Field("changeToResearchInefficiencyMultiplier").GetValue()),
                    };
                    entries.Add(e);
                }

                if (entries.Count > 0)
                    StatsRecorder.RecordTechs(entries.ToArray());
            }
            catch (System.Exception ex) { Main.Log("CollectTechs failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static double ToD(object v)
        {
            if (v == null) return 0;
            try { return System.Convert.ToDouble(v); } catch { return 0; }
        }

        /// <summary>Map Technology.gridType (ETechType int) to a readable category.
        /// Verified against Assembly-CSharp.dll: ETechType is
        ///   0=transmission, 1=ability, 2=symptom, 3=all.
        /// (The earlier mapping had this exactly reversed.)</summary>
        private static string TechCategoryOf(object tech)
        {
            try
            {
                object v = Traverse.Create(tech).Field("gridType").GetValue();
                if (v == null) return "";
                int g = System.Convert.ToInt32(v);
                switch (g)
                {
                    case 0: return "Transmission";
                    case 1: return "Ability";
                    case 2: return "Symptom";
                    case 3: return "All";
                    default: return "Type " + g;
                }
            }
            catch { return ""; }
        }

        /// <summary>Read all countries, build continent rollups onto the snapshot,
        /// and stash a full per-country array for periodic recording.</summary>
        private static void CollectGeography(int day, DiseaseSnapshot s)
        {
            try
            {
                // World.instance is a public static field; .countries is a List<Country>.
                object world = AccessTools.Field(typeof(World), "instance").GetValue(null);
                if (world == null) return;
                var countriesField = AccessTools.Field(typeof(World), "countries");
                if (countriesField == null) return;
                var list = countriesField.GetValue(world) as System.Collections.IList;
                if (list == null || list.Count == 0) return;

                // Accumulators per continent (index by enum int: 0=NONE..5=ASIA).
                var cInf = new double[6];
                var cDead = new double[6];
                var cPop = new double[6];
                var countrySamples = new System.Collections.Generic.List<CountrySample>(list.Count);

                foreach (var c in list)
                {
                    if (c == null) continue;
                    var sp = c as SPCountry;
                    if (sp == null) continue;

                    int cont = (int)sp.continentType;          // enum -> int
                    long infected = sp.totalInfected;
                    long dead = sp.deadPopulation;
                    long popOrig = sp.originalPopulation;

                    if (cont >= 0 && cont < 6)
                    {
                        cInf[cont] += infected;
                        cDead[cont] += dead;
                        cPop[cont] += popOrig;
                    }

                    countrySamples.Add(new CountrySample
                    {
                        id = sp.id,
                        name = sp.name,           // Country.name field (display name)
                        continent = sp.continentType.ToString(),
                        infected = infected,
                        dead = dead,
                        population = popOrig,
                    });
                }

                // Attach continent rollups to this day's snapshot (skip NONE = index 0).
                var rollups = new System.Collections.Generic.List<ContinentRollup>(5);
                for (int i = 1; i < 6; i++)
                {
                    if (cPop[i] <= 0) continue;
                    rollups.Add(new ContinentRollup
                    {
                        name = ((Country.EContinentType)i).ToString(),
                        infected = cInf[i],
                        dead = cDead[i],
                        population = cPop[i],
                    });
                }
                s.continents = rollups;
                _countrySamples = countrySamples.ToArray();
            }
            catch (System.Exception e) { Main.Log($"CollectGeography failed: {e.Message}"); }
        }


        /// <summary>Read the current in-game day as a simple incrementing counter.
        /// The game advances CGameManager.currentGameDate (a DateTime) once per
        /// in-game day. We remember the last date we saw and bump a counter each
        /// time it advances — far more robust than subtracting from a stored
        /// day-zero (which can be default(DateTime) if the first read threw).</summary>
        private static int ReadDay(SPDisease disease)
        {
            System.DateTime cur;
            try
            {
                // currentGameDate is a STATIC field on CGameManager. Traverse on a Type
                // reads instance members, not static — use AccessTools for static fields.
                cur = (System.DateTime)AccessTools.Field(typeof(CGameManager), "currentGameDate").GetValue(null);
            }
            catch { cur = default(System.DateTime); }

            // First observation of a real (non-default) date: start counting at day 1.
            if (_dayCounter == 0 && cur != default(System.DateTime))
            {
                _lastDate = cur;
                _dayCounter = 1;
                return 1;
            }
            // Each time the date advances past what we last saw, increment the day.
            if (cur != default(System.DateTime) && cur != _lastDate)
            {
                _lastDate = cur;
                _dayCounter++;
            }
            return _dayCounter;
        }

        private static int _dayCounter;
        private static System.DateTime _lastDate;
        private static int _tickFallback;  // tick-based pseudo-day when date is null

        // --- member accessors with graceful fallback ---
        // IMPORTANT: the game exposes these stats as PROPERTIES (get_globalInfectiousness,
        // get_airTransmission, ...), not fields. Traverse.Field() returns null for a
        // property, so try Property() first, then fall back to Field(). The day fields
        // on CGameManager (currentGameDate, startDate) ARE plain static fields.
        private static double? F(object obj, string name)
        {
            try
            {
                object v = Traverse.Create(obj).Property(name).GetValue()
                           ?? Traverse.Create(obj).Field(name).GetValue();
                if (v == null) return null;
                return System.Convert.ToDouble(v);
            }
            catch { return null; }
        }

        private static int T(object obj, string name, int fallback)
        {
            try
            {
                object v = Traverse.Create(obj).Property(name).GetValue()
                           ?? Traverse.Create(obj).Field(name).GetValue();
                if (v == null) return fallback;
                return System.Convert.ToInt32(v);
            }
            catch { return fallback; }
        }
    }
}
