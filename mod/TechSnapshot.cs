using System.Globalization;
using System.Text;

namespace PlagueDash
{
    /// <summary>
    /// One trait's planner entry: what it costs NOW and what it DOES. Serialized as
    /// part of the 'techs' SSE event the dashboard's Trait Planner panel shows.
    /// </summary>
    internal sealed class TechEntry
    {
        public string id;
        public string name;          // Technology.name
        public string category;      // Technology.category / ETechType label
        public double cost;          // current evolve cost (via SPDisease.GetEvolveCost)
        public double infectivity;   // changeToInfectiousness
        public double severity;      // changeToSeverity
        public double lethality;     // changeToLethality
        public double air, sea, land, corpse;          // transmission deltas
        public double wealthy, poverty, urban, rural;  // environment deltas
        public double hot, cold, arid, humid;          // climate deltas
        public double cureMulti;     // changeToCureBaseMultiplier
        public double researchIneff; // changeToResearchInefficiencyMultiplier

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"id\":").Append(JsonStr(id));
            sb.Append(",\"name\":").Append(JsonStr(name));
            sb.Append(",\"category\":").Append(JsonStr(category));
            sb.Append(",\"cost\":").Append(cost.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"inf\":").Append(infectivity.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"sev\":").Append(severity.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"let\":").Append(lethality.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"air\":").Append(air.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"sea\":").Append(sea.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"land\":").Append(land.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"corpse\":").Append(corpse.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"wealthy\":").Append(wealthy.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"poverty\":").Append(poverty.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"urban\":").Append(urban.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"rural\":").Append(rural.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"hot\":").Append(hot.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"cold\":").Append(cold.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"arid\":").Append(arid.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"humid\":").Append(humid.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"cureMulti\":").Append(cureMulti.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"researchIneff\":").Append(researchIneff.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
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
