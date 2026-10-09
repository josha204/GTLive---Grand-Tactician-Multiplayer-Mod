using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GTLive
{
    [BepInPlugin(Guid, "GT Live Multiplayer", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gtlive.multiplayer";
        public const string Version = "0.3.0";

        public static Plugin Instance;
        public static ManualLogSource Log;

        // Set with --gtlive-id=<name>. Lets two copies of the game run from one install
        // (separate scenelog, separate lab folder, separate MP save folder).
        public static string InstanceId = "";
        // Set with --gtlive-lab. Windowed, never writes Config/prefs.dat, enables the lab command channel.
        public static bool LabMode;
        public static bool Mute;
        public static int LabWidth = 1280, LabHeight = 720;

        public static ConfigEntry<string> CfgHostAddress;
        public static ConfigEntry<int> CfgPort;
        public static ConfigEntry<string> CfgPlayerName;
        public static ConfigEntry<bool> CfgShowOverlay;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            ParseArgs();
            if (LabMode) BepInEx.Logging.Logger.Listeners.Add(new LabLogListener(InstanceId));

            CfgHostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1", "Address the guest connects to.");
            CfgPort = Config.Bind("Network", "Port", 27455, "TCP port the host listens on.");
            CfgPlayerName = Config.Bind("Network", "PlayerName", Environment.UserName, "Name shown to the other player.");
            CfgShowOverlay = Config.Bind("UI", "ShowWindow", true, "Show the GT Live window (F8 toggles it and remembers the choice).");
            Overlay.Visible = CfgShowOverlay.Value;

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Patches.HumanPatches.Apply(harmony);
            Patches.BattlePatches.Apply(harmony);

            // The game reloads the "Main" scene non-additively on every scene change, so the
            // runner lives on its own DontDestroyOnLoad object.
            var go = new GameObject("GTLive_Runner");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();

            Log.LogInfo($"GTLive {Version} loaded. id='{InstanceId}' lab={LabMode}");
        }

        private static void ParseArgs()
        {
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a.StartsWith("--gtlive-id=", StringComparison.OrdinalIgnoreCase))
                    InstanceId = a.Substring("--gtlive-id=".Length).Trim();
                else if (a.Equals("--gtlive-lab", StringComparison.OrdinalIgnoreCase))
                    LabMode = true;
                else if (a.Equals("--gtlive-mute", StringComparison.OrdinalIgnoreCase))
                    Mute = true;
                else if (a.StartsWith("--gtlive-size=", StringComparison.OrdinalIgnoreCase))
                {
                    var p = a.Substring("--gtlive-size=".Length).Split('x');
                    if (p.Length == 2 && int.TryParse(p[0], out var w) && int.TryParse(p[1], out var h))
                    {
                        LabWidth = w;
                        LabHeight = h;
                    }
                }
            }
        }

        public static string GameRoot => Path.GetDirectoryName(Application.dataPath);
    }

    /// <summary>Lab only: this instance's own log (the BepInEx log file is shared by both instances).</summary>
    internal class LabLogListener : ILogListener
    {
        private readonly string _path;
        private int _lines;

        public LabLogListener(string id)
        {
            string dir = Path.Combine(@"C:\gtlive\lab", string.IsNullOrEmpty(id) ? "main" : id);
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "plugin.log");
            File.WriteAllText(_path, "");
        }

        public void LogEvent(object sender, LogEventArgs e)
        {
            bool mine = e.Source?.SourceName == "GT Live Multiplayer";
            bool bad = (e.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0;
            if (!mine && !bad) return;
            if (++_lines > 20000) return;
            try { File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} [{e.Level}:{e.Source?.SourceName}] {e.Data}\n"); }
            catch (IOException) { }
        }

        public void Dispose() { }
    }

    public class Runner : MonoBehaviour
    {
        private void Update()
        {
            if (Plugin.Mute) AudioListener.volume = 0f;
            try { Session.Tick(); } catch (Exception e) { Plugin.Log.LogError("Session.Tick: " + e); }
            try { Lab.Tick(); } catch (Exception e) { Plugin.Log.LogError("Lab.Tick: " + e); }
        }

        private void OnGUI()
        {
            try { Overlay.Draw(); } catch (Exception e) { Plugin.Log.LogError("Overlay: " + e); }
        }

        private void OnApplicationQuit()
        {
            Session.Shutdown();
        }
    }
}
