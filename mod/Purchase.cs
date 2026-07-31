using System.Globalization;
using System.Text;

namespace PlagueDash
{
    /// <summary>
    /// One evolve/devolve event: a trait bought or refunded with DNA. Emitted as
    /// a 'purchase' SSE event the instant the player evolves/devolves a Technology.
    /// </summary>
    internal sealed class Purchase
    {
        public int day;            // in-game day when it happened
        public string action;      // "evolve" or "devolve"
        public string name;        // Technology.name (display name)
        public string category;    // Technology.category / gridType label
        public double cost;        // DNA charged (>=0 for evolve, shown as the spend;
                                   // for devolve this is the refund amount, reported >=0)

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"day\":").Append(day);
            sb.Append(",\"action\":").Append(JsonStr(action));
            sb.Append(",\"name\":").Append(JsonStr(name));
            sb.Append(",\"category\":").Append(JsonStr(category));
            sb.Append(",\"cost\":").Append(cost.ToString("R", CultureInfo.InvariantCulture));
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
