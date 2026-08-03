using System;
using HarmonyLib;

namespace PlagueDash
{
    /// <summary>
    /// Detects whether the current game session is a multiplayer game (Versus MP
    /// or Co-op MP). Plague Dash is a **single-player-only** mod: using it in
    /// multiplayer gives a competitive information advantage and could result in
    /// a ban. When a multiplayer session is detected, all data-collection patches
    /// short-circuit, the dashboard stops streaming, and the UI shows a warning.
    ///
    /// The check reads <c>CGameManager.IsMultiplayerGame</c> — a public static
    /// bool property that covers both VersusMP and CoopMP game types. Falls back
    /// to reading the static <c>gameType</c> field (enum: VersusMP=6, CoopMP=7).
    /// </summary>
    internal static class MultiplayerGuard
    {
        private static bool _warned;

        /// <summary>True if the current game is any kind of multiplayer session.
        /// Cached per-check; returns false on read failure (the data patches also
        /// guard on this, so a thrown exception won't leak MP data).</summary>
        public static bool IsMultiplayer
        {
            get
            {
                try
                {
                    // Primary: CGameManager.IsMultiplayerGame static bool property.
                    object mp = Traverse.Create(typeof(CGameManager))
                                   .Property("IsMultiplayerGame").GetValue();
                    if (mp != null)
                    {
                        bool isMp = Convert.ToBoolean(mp);
                        if (isMp && !_warned)
                        {
                            _warned = true;
                            Main.Log("Multiplayer game detected — Plague Dash is single-player only. "
                                   + "Data collection disabled to avoid competitive-advantage / ban risk.");
                        }
                        return isMp;
                    }
                    // Fallback: static gameType field (VersusMP=6, CoopMP=7).
                    object gt = AccessTools.Field(typeof(CGameManager), "gameType").GetValue(null);
                    if (gt != null)
                    {
                        int t = Convert.ToInt32(gt);
                        return t == 6 || t == 7;
                    }
                }
                catch (Exception e)
                {
                    Main.Log("MultiplayerGuard check failed: " + e.Message);
                }
                return false;
            }
        }
    }
}
