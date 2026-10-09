using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GTLive.Net;
using GTLive.Sync;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// When the two players' armies meet, both get the base game's battle panel (Play battle / Autoresolve / Defend /
    /// Withdraw / Retreat, plus which of their formations take part). The host combines the two answers:
    ///   - a Withdraw or Retreat always happens for whoever chose it, and then there is no battle;
    ///   - if either chose Play, the battle is fought in 3D (a player who didn't choose Play has their army run by the
    ///     battle AI and spectates, and can take command at any time);
    ///   - both Defend: no battle, both dig in;
    ///   - otherwise the battle is autoresolved on the campaign map (Defend digs that side in first).
    /// </summary>
    public static class BattleDecision
    {
        public const int Play = 3, Autoresolve = 5, Defend = 6, Withdraw = 7, Retreat = 8;

        public static bool Pending;          // host: waiting for answers / guest: prompt on screen or waiting for result
        public static bool Resolving;        // host: our own SetBattlePanelShownState calls pass through
        public static int HostChoice = -1, GuestChoice = -1;
        public static string MonumentKey = "";
        public static string StatusText = "";
        private static int _id;
        private static bool _wasPaused;
        private static List<int> _guestActive = new List<int>();

        // decided roles of the 3D battle that is about to start
        public static bool HostPlays, GuestPlays;

        public static string ChoiceName(int c)
        {
            switch (c)
            {
                case Play: return "play the battle";
                case Autoresolve: return "autoresolve";
                case Defend: return "defend";
                case Withdraw: return "withdraw";
                case Retreat: return "retreat";
                default: return "...";
            }
        }

        // ---- access to the game's battle panel --------------------------------------------------------

        private static BattleUI Bui => GameApi.BU?.GetComponent<BattleUI>();
        private static PanelHandler Ph => GameObject.Find("UI")?.GetComponent<PanelHandler>();
        private static BattlePanel Bpanel => GameApi.BU != null ? Traverse.Create(GameApi.BU).Field("bpanel").GetValue<BattlePanel>() : null;

        private static IList Entries => AccessTools.Field(typeof(BattlePanel), "bpunitentry").GetValue(null) as IList;

        private struct Entry
        {
            public Regiment Reg;
            public int Side;
            public CheckBox Box;
        }

        private static List<Entry> ReadEntries()
        {
            var list = new List<Entry>();
            var raw = Entries;
            if (raw == null) return list;
            foreach (var e in raw)
            {
                var t = Traverse.Create(e);
                list.Add(new Entry
                {
                    Reg = t.Field("regref").GetValue<Regiment>(),
                    Side = t.Field("side").GetValue<int>(),
                    Box = t.Field("checkbox").GetValue<CheckBox>(),
                });
            }
            return list;
        }

        private static string PanelText()
        {
            var ph = Ph;
            try
            {
                var tr = ph.panel[ph.cpanel_battle].go.transform.Find("Background/TextSmall");
                return tr != null ? tr.GetComponent<TextMeshProUGUI>()?.text ?? "" : "";
            }
            catch { return ""; }
        }

        private static BattleMonument PendingMonument()
        {
            var bp = Bpanel;
            return bp != null ? Traverse.Create(bp).Field("bmonumentref").GetValue<BattleMonument>() : null;
        }

        private static bool InvolvesGuest(List<Entry> entries) => entries.Any(e => e.Reg != null && e.Reg.alliance == Mp.GuestAlliance);

        // ---- host -------------------------------------------------------------------------------------

        /// <summary>Host: the game just opened the battle panel. Returns true if the guest takes part in the decision.</summary>
        public static bool HostBegin()
        {
            var entries = ReadEntries();
            if (!InvolvesGuest(entries)) return false;
            var bp = Bpanel;
            var mon = PendingMonument();
            var data = Traverse.Create(bp).Field("battledata").GetValue<ImportExportUnitData.BattleDataFile>();
            if (mon == null || data == null) return false;

            _id++;
            Pending = true;
            HostChoice = GuestChoice = -1;
            _guestActive.Clear();
            HostPlays = GuestPlays = false;
            MonumentKey = MonumentSync.Key(mon);
            _wasPaused = ClockSync.SharedPaused;
            ClockSync.SetShared(true, ClockSync.SharedSpeed);
            StatusText = "Battle! Choose your orders. Waiting for " + Session.RemoteName + " too.";

            var rec = new StringBuilder();
            using (var sw = new StringWriter(rec)) mon.Save(new StreamWriterShim(sw));
            int id = _id;
            string text = PanelText();
            Session.Send(MsgType.BattlePrompt, Wire.Build(w =>
            {
                w.Write(id);
                w.WriteStr(MonumentKey);
                w.Write(MainMenuControls.currentversion);
                w.WriteStr(rec.ToString());
                w.WriteStr(text);
                // BattleDataFile
                w.WriteStr(data.battlename); w.Write(data.day); w.Write(data.month); w.Write(data.year);
                w.WriteStr(data.location); w.WriteStr(data.location2); w.Write(data.starthour); w.Write(data.startminute);
                w.Write(data.alliance[0]); w.Write(data.alliance[1]);
                w.Write(data.startweather); w.Write(data.starttemperature); w.Write(data.battleposition);
                w.Write(Bui.battlepanellocation);
                w.Write(entries.Count);
                foreach (var e in entries)
                {
                    w.Write(e.Reg != null ? e.Reg.unitid : 0);
                    w.Write(e.Reg != null && e.Reg.isbattlestartunit);
                    w.Write(e.Box == null || e.Box.isactive);
                }
            }));
            Session.Notice("Battle! " + data.battlename + ": waiting for both players' orders.");
            return true;
        }

        /// <summary>Host: the host pressed one of the battle panel's order buttons.</summary>
        public static void HostChose(int choice)
        {
            HostChoice = choice;
            var ph = Ph;
            ph.HidePanel(ph.panel, ph.cpanel_battle);
            StatusText = $"You chose to {ChoiceName(choice)}. Waiting for {Session.RemoteName}...";
            TryResolve();
        }

        public static void OnChoice(BinaryReader r)
        {
            int id = r.ReadInt32();
            if (!Pending || id != _id) return;
            GuestChoice = r.ReadInt32();
            int n = r.ReadInt32();
            _guestActive = new List<int>();
            for (int i = 0; i < n; i++) _guestActive.Add(r.ReadInt32());
            Session.Notice($"{Session.RemoteName} chose to {ChoiceName(GuestChoice)}.");
            TryResolve();
        }

        private static void TryResolve()
        {
            if (!Pending || HostChoice < 0 || GuestChoice < 0) return;
            var bui = Bui;
            var ph = Ph;
            var mon = PendingMonument();
            var entries = ReadEntries();
            int h = HostChoice, g = GuestChoice;
            string outcome;
            Resolving = true;
            try
            {
                // the guest decides which of its own formations join
                foreach (var e in entries)
                    if (e.Reg != null && e.Reg.alliance == Mp.GuestAlliance && e.Box != null)
                        e.Box.Check(_guestActive.Contains(e.Reg.unitid));

                Regiment hostUnit = TopUnit(mon, Mp.HostAlliance), guestUnit = TopUnit(mon, Mp.GuestAlliance);
                bool leave = h == Withdraw || h == Retreat || g == Withdraw || g == Retreat;
                if (leave)
                {
                    float date = mon != null && mon.dateto != null ? mon.dateto.yearfraction : GameVars.GetCampaignFloatingDate();
                    Leave(h, hostUnit, date);
                    Leave(g, guestUnit, date);
                    if (mon != null)
                    {
                        BattleUnits.ResetBattleMarkStatus(mon.autocalc_topunitsparticipatingside0, date);
                        BattleUnits.ResetBattleMarkStatus(mon.autocalc_topunitsparticipatingside1, date);
                        mon.Remove();
                    }
                    ph.HidePanel(ph.panel, ph.cpanel_battle);
                    bui.SetBattlePanelShownState(0);
                    outcome = "No battle: " + (h == Withdraw || h == Retreat ? $"{Session.AllianceName(Mp.HostAlliance)} {(h == Withdraw ? "withdraws" : "retreats")}" : "")
                              + (g == Withdraw || g == Retreat ? $"{(h == Withdraw || h == Retreat ? ", " : "")}{Session.AllianceName(Mp.GuestAlliance)} {(g == Withdraw ? "withdraws" : "retreats")}" : "") + ".";
                    ClockSync.SetShared(_wasPaused, ClockSync.SharedSpeed);
                }
                else if (h == Play || g == Play)
                {
                    if (h == Defend) hostUnit?.SetCampaignStance(1);
                    if (g == Defend) guestUnit?.SetCampaignStance(1);
                    HostPlays = h == Play;
                    GuestPlays = g == Play;
                    BattleLink.Prepare(HostPlays, GuestPlays);
                    bui.SetBattlePanelShownState(Play);
                    outcome = "The battle will be fought in 3D: " + Roles(HostPlays, GuestPlays);
                }
                else if (h == Defend && g == Defend)
                {
                    hostUnit?.SetCampaignStance(1);
                    guestUnit?.SetCampaignStance(1);
                    if (mon != null)
                    {
                        BattleUnits.ResetBattleMarkStatus(mon.autocalc_topunitsparticipatingside0, 1E-05f);
                        BattleUnits.ResetBattleMarkStatus(mon.autocalc_topunitsparticipatingside1, 1E-05f);
                        mon.Remove();
                    }
                    ph.HidePanel(ph.panel, ph.cpanel_battle);
                    bui.SetBattlePanelShownState(0);
                    outcome = "Both armies dig in. No battle for now.";
                    ClockSync.SetShared(_wasPaused, ClockSync.SharedSpeed);
                }
                else
                {
                    if (h == Defend) hostUnit?.SetCampaignStance(1);
                    if (g == Defend) guestUnit?.SetCampaignStance(1);
                    bui.SetBattlePanelShownState(Autoresolve);
                    outcome = "The battle is autoresolved on the campaign map.";
                    ClockSync.SetShared(_wasPaused, ClockSync.SharedSpeed);
                }
            }
            finally
            {
                Resolving = false;
            }
            Pending = false;
            StatusText = "";
            int id = _id;
            Session.Send(MsgType.BattleResolved, Wire.Build(w =>
            {
                w.Write(id);
                w.WriteStr(outcome);
                w.Write(HostPlays);
                w.Write(GuestPlays);
            }));
            Session.Notice(outcome);
        }

        public static string Roles(bool hostPlays, bool guestPlays)
        {
            if (hostPlays && guestPlays) return "both players command their armies.";
            string player = hostPlays ? Session.AllianceName(Mp.HostAlliance) : Session.AllianceName(Mp.GuestAlliance);
            string ai = hostPlays ? Session.AllianceName(Mp.GuestAlliance) : Session.AllianceName(Mp.HostAlliance);
            return $"{player} is commanded by its player, {ai} by the battle AI (its player watches and can take command).";
        }

        private static Regiment TopUnit(BattleMonument mon, int alliance)
        {
            if (mon == null) return null;
            foreach (var list in new[] { mon.autocalc_topunitsparticipatingside0, mon.autocalc_topunitsparticipatingside1 })
                if (list != null && list.Count > 0 && list[0] != null && list[0].alliance == alliance) return list[0];
            return null;
        }

        private static void Leave(int choice, Regiment unit, float date)
        {
            if (unit == null) return;
            var units = new List<Regiment> { unit };
            if (choice == Withdraw) BattleUnits.SetWithdrawal(date, units, -1, default, removemonument: false);
            if (choice == Retreat) BattleUnits.SetRetreat(date, units, -1, default, decisive: false, removemonument: false);
        }

        // ---- guest ------------------------------------------------------------------------------------

        private static int _guestPromptId;
        public static bool GuestPromptOpen;

        public static void OnPrompt(BinaryReader r)
        {
            _guestPromptId = r.ReadInt32();
            MonumentKey = r.ReadString();
            float version = r.ReadSingle();
            string rec = r.ReadString();
            string text = r.ReadString();
            var data = new ImportExportUnitData.BattleDataFile(new int[2]);
            data.battlename = r.ReadString(); data.day = r.ReadInt32(); data.month = r.ReadInt32(); data.year = r.ReadInt32();
            data.location = r.ReadString(); data.location2 = r.ReadString(); data.starthour = r.ReadInt32(); data.startminute = r.ReadInt32();
            data.alliance[0] = r.ReadInt32(); data.alliance[1] = r.ReadInt32();
            data.startweather = r.ReadInt32(); data.starttemperature = r.ReadSingle(); data.battleposition = r.ReadVector3();
            var panelLocation = r.ReadVector3();
            int n = r.ReadInt32();
            var units = new List<KeyValuePair<Regiment, bool>>();
            for (int i = 0; i < n; i++)
            {
                var reg = GameApi.FindUnit(r.ReadInt32());
                bool start = r.ReadBoolean();
                bool active = r.ReadBoolean();
                if (reg == null) continue;
                reg.isbattlestartunit = start;
                units.Add(new KeyValuePair<Regiment, bool>(reg, active));
            }

            var mon = MonumentSync.EnsureMonument(MonumentKey, rec, version);
            if (mon == null) { Session.Notice("Could not show the battle panel; answering 'autoresolve'."); SendChoice(Autoresolve); return; }

            Pending = true;
            GuestPromptOpen = true;
            var bui = Bui;
            var ph = Ph;
            var bp = Bpanel;
            Resolving = true;
            try { bui.SetBattlePanelShownState(1); }
            finally { Resolving = false; }
            ph.ShowPanel(ph.cpanel_battle, 100000000f, null, default, default, mon.gameObject);
            bui.battlepanellocation = panelLocation;
            bp.ClearUnitLines();
            bp.AddBattlefieldData(data, mon);
            foreach (var kv in units) bp.AddUnitLine(kv.Key);
            foreach (var e in ReadEntries())
            {
                var match = units.FirstOrDefault(u => u.Key == e.Reg);
                if (e.Box != null && match.Key != null && !match.Value) e.Box.Check(false);
            }
            if (text.Length > 0) ph.ExchangePanelText(ph.panel[ph.cpanel_battle], "Background/TextSmall", text);
            StatusText = "Battle! Choose your orders. " + Session.RemoteName + " is choosing too.";
            Session.Notice("Battle! " + data.battlename + ": choose your orders.");
        }

        /// <summary>Guest: one of the battle panel's order buttons was pressed.</summary>
        public static void SendChoice(int choice)
        {
            var mine = ReadEntries().Where(e => e.Reg != null && e.Reg.alliance == Mp.GuestAlliance && (e.Box == null || e.Box.isactive)).Select(e => e.Reg.unitid).ToList();
            int id = _guestPromptId;
            Session.Send(MsgType.BattleChoice, Wire.Build(w =>
            {
                w.Write(id);
                w.Write(choice);
                w.Write(mine.Count);
                foreach (var u in mine) w.Write(u);
            }));
            GuestPromptOpen = false;
            var ph = Ph;
            ph?.HidePanel(ph.panel, ph.cpanel_battle);
            var bui = Bui;
            if (bui != null) bui.battlepanelshown = 0;
            StatusText = $"You chose to {ChoiceName(choice)}. Waiting for {Session.RemoteName}...";
        }

        public static void OnResolved(BinaryReader r)
        {
            r.ReadInt32();
            string outcome = r.ReadString();
            HostPlays = r.ReadBoolean();
            GuestPlays = r.ReadBoolean();
            Pending = false;
            GuestPromptOpen = false;
            StatusText = "";
            MonumentSync.Unprotect();
            Session.Notice(outcome);
            if (HostPlays || GuestPlays) BattleLink.Prepare(HostPlays, GuestPlays);
        }

        public static void Reset()
        {
            Pending = Resolving = GuestPromptOpen = false;
            HostChoice = GuestChoice = -1;
            StatusText = "";
        }
    }

    // ---------------------------------------------------------------------------------------------------

    /// <summary>Order buttons on the battle panel: in a session they become this player's answer instead of acting.</summary>
    [HarmonyPatch(typeof(BattleUI), nameof(BattleUI.SetBattlePanelShownState))]
    internal static class BattlePanelChoice
    {
        private static bool Prefix(int newstate)
        {
            if (!Session.IsLive || !GameApi.InCampaignScene || BattleDecision.Resolving) return true;
            bool order = newstate == BattleDecision.Play || newstate == BattleDecision.Autoresolve || newstate == BattleDecision.Defend
                         || newstate == BattleDecision.Withdraw || newstate == BattleDecision.Retreat;
            if (!order) return true;
            if (Mp.Role == Role.Host && BattleDecision.Pending && BattleDecision.HostChoice < 0)
            {
                BattleDecision.HostChose(newstate);
                return false;
            }
            if (Mp.Role == Role.Guest && BattleDecision.GuestPromptOpen)
            {
                BattleDecision.SendChoice(newstate);
                return false;
            }
            return true;
        }
    }
}
