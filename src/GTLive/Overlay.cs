using System;
using GTLive.Sync;
using UnityEngine;

namespace GTLive
{
    /// <summary>F8 toggles a small IMGUI window for hosting, joining and watching the session.</summary>
    public static class Overlay
    {
        public static bool Visible = true;
        // right of the main menu's icon column, below the campaign's top-left message icons
        private static Rect _rect = new Rect(90, 130, 360, 10);
        private static string _address, _port, _name;
        private static GUIStyle _small, _title, _status;
        private static bool _wasLive;

        public static void Draw()
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F8)
            {
                Visible = !Visible;
                if (Plugin.CfgShowOverlay != null) Plugin.CfgShowOverlay.Value = Visible;
                Event.current.Use();
            }
            // pop the window back up when a session goes live or drops
            bool live = Session.IsLive;
            if (live != _wasLive && Session.State != SessionState.Off) Visible = true;
            if (live != _wasLive && !live && Mp.Role != Role.None) Visible = true;
            _wasLive = live;
            if (!Visible) return;

            if (_small == null)
            {
                _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
                _title = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
                _status = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
                _address = Plugin.CfgHostAddress.Value;
                _port = Plugin.CfgPort.Value.ToString();
                _name = Plugin.CfgPlayerName.Value;
            }
            _rect.height = 10; // let GUILayout size it
            _rect = GUILayout.Window(0x67746c, _rect, Window, "GT Live  (F8)");
        }

        private static void Window(int id)
        {
            var st = Session.State;
            GUILayout.Label(Headline(), _title);
            if (Session.LastError.Length > 0 && st == SessionState.Off)
                GUILayout.Label("Last error: " + Session.LastError, _small);

            try
            {
                if (st == SessionState.Off) OffControls();
                else SessionControls();
            }
            catch (Exception e)
            {
                Session.Notice("Error: " + e.Message);
            }

            if (Session.Notices.Count > 0)
            {
                GUILayout.Space(4);
                int from = Math.Max(0, Session.Notices.Count - 6);
                for (int i = from; i < Session.Notices.Count; i++) GUILayout.Label(Session.Notices[i], _small);
            }
            GUI.DragWindow();
        }

        private static string Headline()
        {
            switch (Session.State)
            {
                case SessionState.Off: return "Not connected";
                case SessionState.Hosting: return "Hosting - waiting for a guest";
                case SessionState.Connecting: return "Connecting...";
                case SessionState.Handshake: return "Connected, setting up...";
                case SessionState.Syncing: return Mp.Role == Role.Host ? "Sending campaign..." : "Receiving campaign...";
                case SessionState.Loading: return "Loading the shared campaign...";
                case SessionState.WaitingPeer: return "Waiting for the other player to finish loading...";
                case SessionState.Live:
                    return $"LIVE  -  you: {Session.AllianceName(Mp.LocalAlliance)}   {Session.RemoteName}: {Session.AllianceName(Mp.RemoteAlliance)}";
            }
            return Session.State.ToString();
        }

        private static void OffControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(60));
            _name = GUILayout.TextField(_name, 32);
            GUILayout.EndHorizontal();

            if (GameApi.CampaignReady)
            {
                GUILayout.Label($"Host this campaign. You play {Session.AllianceName(GameVars.playeralliance)}, the guest plays {Session.AllianceName(1 - GameVars.playeralliance)}.", _small);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(60));
                _port = GUILayout.TextField(_port, 6, GUILayout.Width(70));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Battles", GUILayout.Width(60));
                if (GUILayout.Toggle(Session.Battles == BattleMode.Ask, "Ask both players", GUILayout.Width(130))) Session.Battles = BattleMode.Ask;
                if (GUILayout.Toggle(Session.Battles == BattleMode.Autoresolve, "Always autoresolve")) Session.Battles = BattleMode.Autoresolve;
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Host"))
                {
                    SaveFields();
                    Session.StartHost(Plugin.CfgPort.Value);
                }
                GUILayout.Space(6);
            }
            GUILayout.Label("Join a friend's campaign (works from the main menu):", _small);
            GUILayout.BeginHorizontal();
            _address = GUILayout.TextField(_address, 64);
            _port = GUILayout.TextField(_port, 6, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Join"))
            {
                SaveFields();
                Session.Join(Plugin.CfgHostAddress.Value, Plugin.CfgPort.Value);
            }
        }

        private static void SessionControls()
        {
            if (Session.IsLive)
            {
                var c = $"Clock: {(ClockSync.SharedPaused ? "paused" : ClockSync.SharedSpeed.ToString("0.#") + "x")}   ping {ClockSync.RttSeconds * 1000f:0} ms";
                if (Mp.Role == Role.Guest) c += $"   drift {ClockSync.DriftHours * 60:+0;-0;0} min";
                GUILayout.Label(c, _status);
                if (!Battle.BattleLink.Active)
                {
                    GUILayout.Label($"Orders sent {Orders.Sent}, received {Orders.Received}", _small);
                    if (Mp.Role == Role.Guest)
                        GUILayout.Label($"Map drift: avg {UnitStates.AvgDrift:0.0}, max {UnitStates.MaxDrift:0.0} ({UnitStates.MaxDriftUnit})", _small);
                    GUILayout.Label("Battles: " + (Session.Battles == BattleMode.Autoresolve ? "always autoresolved" : "both players choose"), _small);
                }
                if (Battle.BattleDecision.StatusText.Length > 0) GUILayout.Label(Battle.BattleDecision.StatusText, _status);
                if (Battle.BattleLink.Active) Battle.BattleOverlay.Draw(_status, _small);
            }
            GUILayout.BeginHorizontal();
            if (Session.IsLive && !Battle.BattleLink.Active && GUILayout.Button(Mp.Role == Role.Host ? "Resync guest" : "Ask for resync"))
                Session.RequestResync("button");
            if (GUILayout.Button("Leave")) Session.Leave("left");
            GUILayout.EndHorizontal();
        }

        private static void SaveFields()
        {
            Plugin.CfgPlayerName.Value = string.IsNullOrWhiteSpace(_name) ? Environment.UserName : _name.Trim();
            Plugin.CfgHostAddress.Value = _address.Trim();
            if (int.TryParse(_port, out var p) && p > 0 && p < 65536) Plugin.CfgPort.Value = p;
        }
    }
}
