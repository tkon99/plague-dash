using System;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;
using UnityEngine;

namespace PlagueDash
{
    /// <summary>
    /// UMM entry point. Boots Harmony patching, starts/stops the embedded SSE
    /// dashboard server, and wires up the settings panel. All gameplay interaction
    /// happens in the Harmony patches (Patches/*.cs); all data flows through the
    /// in-memory LiveState which the server streams.
    /// </summary>
    public static class Main
    {
        // UMM's logger is an Action<string>; store it so patches can log.
        public static UnityModManager.ModEntry ModEntry;
        public static bool Enabled;
        public static Settings Settings;

        public static void Log(string msg) => ModEntry?.Logger?.Log(msg);

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            Log($"Loaded. Dashboard will serve on http://localhost:{Settings.Port}");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            if (value)
            {
                // Apply all [HarmonyPatch] classes in this assembly.
                var harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log("Patches applied.");
                // Start the embedded dashboard server (serves UI + SSE stream).
                DashboardServer.Start(Settings.Port);
                // Spawn the main-menu overlay button (gated to only show on the menu).
                MenuButtonOverlay.EnsureExists();
            }
            else
            {
                // Stop the server first, then unpatch.
                DashboardServer.Stop();
                MenuButtonOverlay.DestroyIfExists();
                new Harmony(modEntry.Info.Id).UnpatchAll(modEntry.Info.Id);
                Log("Patches removed.");
            }
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Draw(modEntry);

            // In-menu controls below the settings panel.
            GUILayout.BeginVertical("box");
            GUILayout.Label("Dashboard", GUI.skin.label);
            GUILayout.Label("Open http://localhost:" + Settings.Port + "/ in your browser, or click below:",
                GUI.skin.label);
            if (GUILayout.Button("Open Dashboard in Browser", GUILayout.Height(30)))
            {
                // Make sure the server is up (it starts OnToggle), then launch.
                if (DashboardServer.WaitUntilListening(2000))
                    DashboardServer.OpenInBrowser(Settings.Port);
                else
                    Main.Log("Open button clicked but server not listening on port " + Settings.Port);
            }
            GUILayout.EndVertical();
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry);
        }
    }

    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        [Draw("Dashboard port")]
        public int Port = 8765;
        [Draw("Record samples", Collapsible = false)]
        public bool Recording = true;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange() { }
    }
}


