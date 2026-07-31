using HarmonyLib;

namespace PlagueDash.Patches
{
    /// <summary>
    /// Captures each evolve/devolve as a discrete DNA-spend event. Hooks the
    /// player-facing trait purchase methods on the Disease base class:
    ///   - Disease.EvolveTech(Technology, bool)    → spent DNA on a trait
    ///   - Disease.DeEvolveTech(Technology, bool)  → refunded DNA (devolve)
    ///
    /// Each postfix reads the Technology argument's name/category/cost and emits a
    /// 'purchase' record to the live state, which the SSE server streams to the
    /// dashboard feed. The day is read from the DiseaseStatsPatch day counter so
    /// purchases line up with the per-day samples.
    /// </summary>
    [HarmonyPatch(typeof(Disease), "EvolveTech")]
    internal static class EvolveTechPatch
    {
        private static void Postfix(Disease __instance, Technology tech)
        {
            if (!Main.Enabled || !Main.Settings.Recording) return;
            DnaTrackingHelpers.EmitPurchase(__instance, tech, "evolve");
        }
    }

    [HarmonyPatch(typeof(Disease), "DeEvolveTech")]
    internal static class DeEvolveTechPatch
    {
        private static void Postfix(Disease __instance, Technology tech)
        {
            if (!Main.Enabled || !Main.Settings.Recording) return;
            DnaTrackingHelpers.EmitPurchase(__instance, tech, "devolve");
        }
    }

    internal static class DnaTrackingHelpers
    {
        /// <summary>Build a Purchase from a Technology and record it.</summary>
        internal static void EmitPurchase(Disease disease, Technology tech, string action)
        {
            if (tech == null) return;
            try
            {
                string name = Str(tech, "name");
                // category field is empty at runtime; gridType (ETechType enum)
                // is the reliable per-trait category.
                string category = TechCategory(tech);
                double cost = Num(tech, "cost");

                var p = new Purchase
                {
                    day = DiseaseStatsPatch.CurrentDay,
                    action = action,
                    name = name,
                    category = category,
                    cost = cost,
                };
                StatsRecorder.RecordPurchase(p);
            }
            catch (System.Exception e) { Main.Log("EmitPurchase failed: " + e.Message); }
        }

        /// <summary>Map Technology.gridType (ETechType int) to a readable category.
        /// Standard ETechType layout: 0=Symptom, 1=Transmission, 2=Ability.
        /// Falls back to the raw value if unknown.</summary>
        private static string TechCategory(object tech)
        {
            try
            {
                object v = Traverse.Create(tech).Field("gridType").GetValue();
                if (v == null) return "";
                int g = System.Convert.ToInt32(v);
                switch (g)
                {
                    case 0: return "Symptom";
                    case 1: return "Transmission";
                    case 2: return "Ability";
                    default: return "Type " + g;
                }
            }
            catch { return ""; }
        }

        private static string Str(object obj, string name)
        {
            try
            {
                string v = Traverse.Create(obj).Property(name).GetValue<string>();
                if (!string.IsNullOrEmpty(v)) return v;
                return Traverse.Create(obj).Field(name).GetValue<string>() ?? "";
            }
            catch { return ""; }
        }

        private static double Num(object obj, string name)
        {
            try
            {
                object v = Traverse.Create(obj).Property(name).GetValue()
                           ?? Traverse.Create(obj).Field(name).GetValue();
                return v == null ? 0 : System.Convert.ToDouble(v);
            }
            catch { return 0; }
        }
    }
}
