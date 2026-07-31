using System.Globalization;
using System.Text;

namespace PlagueDash
{
    /// <summary>
    /// A full per-country breakdown, written to countries.jsonl (one line per
    /// recording). Each line is {"day":N,"countries":[{...}, ...]} capturing every
    /// country's infected/dead/population/continent at a point in time.
    ///
    /// Written less often than the per-day disease line (e.g. every ~25 days or on
    /// run end) because it's large (~100 countries). The dashboard reads the most
    /// recent line to render the current geographic state.
    /// </summary>
    internal static class CountrySnapshot
    {
        public static string ToJson(int day, CountrySample[] countries)
        {
            var sb = new StringBuilder();
            sb.Append("{\"day\":").Append(day).Append(",\"countries\":[");
            for (int i = 0; i < countries.Length; i++)
            {
                if (i > 0) sb.Append(',');
                countries[i].AppendJson(sb);
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    /// <summary>One country's data for a country snapshot.</summary>
    internal struct CountrySample
    {
        public string id;          // "GBR"
        public string name;        // "United Kingdom"
        public string continent;   // "EUROPE"
        public long infected;      // cumulative
        public long dead;
        public long population;    // original

        public void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"id\":").Append(JsonStr(id));
            sb.Append(",\"name\":").Append(JsonStr(name));
            sb.Append(",\"continent\":").Append(JsonStr(continent));
            sb.Append(",\"inf\":").Append(infected.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"dead\":").Append(dead.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"pop\":").Append(population.ToString(CultureInfo.InvariantCulture));
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
