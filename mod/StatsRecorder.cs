using System;
using System.Text;

namespace PlagueDash
{
    /// <summary>
    /// Thin facade over <see cref="LiveState"/>. The GameUpdate / run-lifecycle
    /// patches call these methods; they now publish to the in-memory store that
    /// the SSE server streams from, instead of writing files to disk.
    ///
    /// Method signatures are preserved from the old disk-based version so the
    /// patches don't change.
    /// </summary>
    internal static class StatsRecorder
    {
        /// <summary>
        /// Called when a new run begins: reset the live state + set run metadata.
        /// </summary>
        public static void StartRun(string plagueType, string difficulty, string startCountry)
        {
            try
            {
                LiveState.Reset();
                LiveState.SetMeta(BuildMeta(plagueType, difficulty, startCountry, "in_progress"));
            }
            catch (Exception e) { Main.Log($"StartRun failed: {e.Message}"); }
        }

        /// <summary>Finalize the run meta (win/lose). Called when the run ends.</summary>
        public static void EndRun(bool won)
        {
            // We don't retain the typed meta fields, so just flip status in-place.
            // The dashboard treats status as informational only.
            try
            {
                LiveState.SetMeta("{\"status\":\"" + (won ? "won" : "lost") + "\"}");
            }
            catch (Exception e) { Main.Log($"EndRun failed: {e.Message}"); }
        }

        /// <summary>Publish one day's sample to the live store (deduped by day).</summary>
        public static void RecordSample(DiseaseSnapshot s)
        {
            try { LiveState.Publish(s); }
            catch (Exception e) { Main.Log($"RecordSample failed: {e.Message}"); }
        }

        /// <summary>Publish a full per-country snapshot. The patch throttles this.</summary>
        public static void RecordCountries(int day, CountrySample[] countries, bool force = false)
        {
            try { LiveState.SetCountries(day, countries); }
            catch (Exception e) { Main.Log($"RecordCountries failed: {e.Message}"); }
        }

        /// <summary>Publish an evolve/devolve event to the purchase feed.</summary>
        public static void RecordPurchase(Purchase p)
        {
            try { LiveState.PublishPurchase(p); }
            catch (Exception e) { Main.Log($"RecordPurchase failed: {e.Message}"); }
        }

        /// <summary>Publish the current Trait Planner list.</summary>
        public static void RecordTechs(TechEntry[] techs)
        {
            try { LiveState.SetTechs(techs); }
            catch (Exception e) { Main.Log($"RecordTechs failed: {e.Message}"); }
        }

        // Build a meta.json-formatted string.
        private static string BuildMeta(string plagueType, string difficulty, string startCountry, string status)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"plagueType\":").Append(Json(plagueType)).Append(",");
            sb.Append("\"difficulty\":").Append(Json(difficulty)).Append(",");
            sb.Append("\"startCountry\":").Append(Json(startCountry)).Append(",");
            sb.Append("\"runStart\":").Append(Json(DateTime.Now.ToString("s"))).Append(",");
            sb.Append("\"status\":").Append(Json(status));
            sb.Append("}");
            return sb.ToString();
        }

        // Minimal JSON string escape.
        private static string Json(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
