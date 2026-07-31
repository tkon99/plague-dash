using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlagueDash
{
    /// <summary>
    /// A single point-in-time sample of disease stats, mirroring the JSON Lines
    /// contract documented in the project README. Fields are nullable so a missing
    /// or unreadable value serializes to an absent key (the dashboard tolerates gaps).
    /// </summary>
    internal sealed class DiseaseSnapshot
    {
        // Key
        public int day;

        // Population (optional — read if available)
        public double? infected;
        public double? dead;

        // Plague stats
        public double? inf, sev, let;

        // Cure
        public double? cureNeed;      // cureRequirements
        public double? cureSpd;       // effective cure research this turn
        public double? curePct;       // 0..1 cure progress

        // Mutation
        public double? mutCnt;

        // DNA economy (optional): current balance, total earned, total spent.
        public double? dna, dnaEarned, dnaSpent;

        // Transmission effectiveness (optional)
        public double? airT, seaT, landT, corpseT;

        // Per-continent rollups (infected/dead per continent). Cheap to record
        // every day (6 continents) and powers the continent chart.
        public List<ContinentRollup> continents;

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"day\":").Append(day);
            Append(sb, "infected", infected);
            Append(sb, "dead", dead);
            Append(sb, "inf", inf);
            Append(sb, "sev", sev);
            Append(sb, "let", let);
            Append(sb, "cureNeed", cureNeed);
            Append(sb, "cureSpd", cureSpd);
            Append(sb, "curePct", curePct);
            Append(sb, "mutCnt", mutCnt);
            Append(sb, "dna", dna);
            Append(sb, "dnaEarned", dnaEarned);
            Append(sb, "dnaSpent", dnaSpent);
            Append(sb, "airT", airT);
            Append(sb, "seaT", seaT);
            Append(sb, "landT", landT);
            Append(sb, "corpseT", corpseT);

            // continents: [{n:"AFRICA",inf:123,dead:4,pop:9e8}, ...]
            if (continents != null && continents.Count > 0)
            {
                sb.Append(",\"continents\":[");
                for (int i = 0; i < continents.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    continents[i].AppendJson(sb);
                }
                sb.Append(']');
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string key, double? v)
        {
            if (!v.HasValue) return;
            sb.Append(",\"").Append(key).Append("\":");
            // invariant culture so decimals use '.', matching the dashboard parser
            sb.Append(v.Value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A continent's totals for one day.</summary>
    internal sealed class ContinentRollup
    {
        public string name;     // "AFRICA" etc.
        public double infected; // cumulative infected this continent
        public double dead;
        public double population; // original population (denominator)

        public void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"n\":").Append(JsonStr(name));
            sb.Append(",\"inf\":").Append(infected.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"dead\":").Append(dead.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"pop\":").Append(population.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        private static string JsonStr(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
