using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GTLive.Net;
using GTLive.Sync;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// Runs a shared 3D battle once both players are on the field: the shared clock, deployment, unit state
    /// (BattleState), orders (BattleOrders), orders only the host can carry out, and taking command of an army
    /// the AI was running.
    /// </summary>
    public static class BattleSync
    {
        // set by BattlePatches
        public static bool InUi, InOrderProcessing, InGameTimeLogic;
        /// <summary>The speed the local player picked when they ended deployment.</summary>
        public static float PreferredSpeed = 1f;

        // this game's units by its own unit id
        private static readonly Dictionary<int, Regiment> Local = new Dictionary<int, Regiment>();
        private static readonly List<Regiment> LocalList = new List<Regiment>();
        // Each game numbers a battle's units from its own player's point of view, so the same regiment has
        // different ids on host and guest. On the wire every unit goes by the host's id; the guest maps the
        // host's roster onto its own units by side, type, name and position (identical at load).
        private static readonly Dictionary<int, Regiment> ByNet = new Dictionary<int, Regiment>();
        private static readonly Dictionary<Regiment, int> NetOfLocal = new Dictionary<Regiment, int>();
        private static readonly List<Regiment> NetList = new List<Regiment>();
        private static readonly List<RosterEntry> Unmatched = new List<RosterEntry>();
        private static readonly HashSet<int> RosterSent = new HashSet<int>();
        public static int DuplicateIds, RosterMatched, RosterUnmatched;
        private static float _nextRegistry;
        private static bool _started;

        private class RosterEntry
        {
            public int Id, Alliance, Typ;
            public string Name;
            public Vector3 Pos;
        }

        private static bool _localDeployReady, _remoteDeployReady, _wasDeploying;
        private static float _remotePreferredSpeed = 1f;
        private static float _nextPlacement, _lastSpectatorNotice;
        private static readonly Dictionary<int, KeyValuePair<Vector3, float>> SentPlacement = new Dictionary<int, KeyValuePair<Vector3, float>>();

        private const byte EvDeployReady = 1, EvDeployEnd = 2, EvDeployPlace = 3, EvTakeCommand = 4, EvTarget = 6, EvCharge = 7, EvEnterBuilding = 8, EvRoster = 9, EvPromptContinue = 10, EvArmyOrder = 11, EvArrival = 12;
        public const byte EvPontoons = 13, EvPontoonOrder = 14, EvObstacles = 15, EvObstacleNew = 16, EvCeaseFire = 17, EvCeaseFireAnswer = 18, EvArrivalTimes = 19;

        public enum ArmyOrder : byte { Retreat = 1, NightWithdraw = 2, Surrender = 3 }

        public static bool ClockActive => BattleLink.IsLive && BattleLink.BattleSceneReady && _started;
        private static bool Deploying => GameApi.BUI != null && GameApi.BUI.deploymentmodeactive;

        public static void Reset()
        {
            _started = false;
            Local.Clear();
            LocalList.Clear();
            ByNet.Clear();
            NetOfLocal.Clear();
            NetList.Clear();
            Unmatched.Clear();
            RosterSent.Clear();
            RosterMatched = RosterUnmatched = 0;
            SentPlacement.Clear();
            _localDeployReady = _remoteDeployReady = _wasDeploying = false;
            InUi = InOrderProcessing = InGameTimeLogic = false;
            PreferredSpeed = 1f;
            ClockJumps = 0;
            _lastHostPrompt = _lastMyPrompt = -1;
            _shownEndBattle = -1;
            _shownRetreat = false;
            BattleOrders.Reset();
            BattleState.Reset();
            BattleEngineering.Reset();
            BattleCeaseFire.Reset();
            Arrivals = 0;
            _nextArrivalTimes = 0f;
        }

        /// <summary>Called when both players are on the field; the real start waits for the scene (Tick).</summary>
        public static void Start() => _started = false;

        public static void Tick()
        {
            if (!BattleLink.BattleSceneReady) return;
            if (!_started) Begin();
            if (Time.unscaledTime >= _nextRegistry) RebuildRegistry();
            if (Mp.Role == Role.Host)
            {
                ClockSync.HostTick();
                HostDeployment();
                BattleState.HostTick();
                BattleOrders.Tick();
                BattleEngineering.HostTick();
                BattleCeaseFire.HostTick();
                HostArrivalTimes();
            }
            else
            {
                ClockSync.GuestTick();
                GuestClockJump();
                GuestPrompts();
                GuestOutcome();
                GuestDeployment();
                BattleOrders.Tick();
                // the guest's game never ends a day itself; a confirmed cease fire becomes a proposal to the host
                var bu = GameApi.BU;
                if (bu != null && bu.triggerendofday)
                {
                    bu.triggerendofday = false;
                    if (BattleRules.GuestCommands) BattleCeaseFire.GuestProposes();
                }
            }
        }

        // ---- reinforcements -------------------------------------------------------------------------------

        public static int Arrivals;
        private static readonly MethodInfo SetGroupArrival = AccessTools.Method(typeof(BattleUnits), "SetGroupArrival");

        /// <summary>Host: a reinforcement group is about to march onto the field (BattleUnits.SetGroupArrival).</summary>
        public static void HostArrival(BattleUnits bu, int index, bool allImmediately)
        {
            if (index < 0 || index >= bu.scheduledarrival.Count) return;
            var s = bu.scheduledarrival[index];
            int group = NetId(s.regimentrefreg);
            int entry = s.entrypoint != null && bu.entrypoints != null ? System.Array.IndexOf(bu.entrypoints, s.entrypoint) : -1;
            if (group < 0) return;
            SendEvent(EvArrival, w => { w.Write(group); w.Write(entry); w.Write(allImmediately); });
            Arrivals++;
        }

        private static float _nextArrivalTimes;

        /// <summary>Host: how long until each reinforcement group arrives, and where (the guest's own countdown is off).</summary>
        private static void HostArrivalTimes()
        {
            var bu = GameApi.BU;
            if (bu?.scheduledarrival == null || bu.scheduledarrival.Count == 0 || Time.unscaledTime < _nextArrivalTimes) return;
            _nextArrivalTimes = Time.unscaledTime + 3f;
            var list = new List<BattleUnits.ScheduledArrival>(bu.scheduledarrival);
            SendEvent(EvArrivalTimes, w =>
            {
                w.Write((short)list.Count);
                foreach (var s in list)
                {
                    w.Write(NetId(s.regimentrefreg));
                    w.Write(s.durationleft);
                    w.Write((short)(s.entrypoint != null && bu.entrypoints != null ? System.Array.IndexOf(bu.entrypoints, s.entrypoint) : -1));
                }
            });
        }

        private static void OnArrivalTimes(BinaryReader rd)
        {
            var bu = GameApi.BU;
            int n = rd.ReadInt16();
            for (int i = 0; i < n; i++)
            {
                var reg = Find(rd.ReadInt32());
                float left = rd.ReadSingle();
                int entry = rd.ReadInt16();
                if (bu == null || reg == null) continue;
                var s = bu.scheduledarrival.Find(x => x.regimentrefreg == reg);
                if (s == null) continue;
                s.durationleft = left;
                if (entry >= 0 && bu.entrypoints != null && entry < bu.entrypoints.Length) s.entrypoint = bu.entrypoints[entry];
            }
        }

        /// <summary>Guest: the same group arrives here, at the entry point the host used.</summary>
        private static void OnArrival(BinaryReader rd)
        {
            int group = rd.ReadInt32();
            int entry = rd.ReadInt32();
            bool all = rd.ReadBoolean();
            var bu = GameApi.BU;
            var reg = Find(group);
            if (bu == null || reg == null) return;
            int index = bu.scheduledarrival.FindIndex(s => s.regimentrefreg == reg);
            if (index < 0)
            {
                Plugin.Log.LogWarning($"reinforcement arrival: no scheduled arrival for {reg.name} on this side");
                return;
            }
            if (entry >= 0 && bu.entrypoints != null && entry < bu.entrypoints.Length) bu.scheduledarrival[index].entrypoint = bu.entrypoints[entry];
            Mp.Applying = true;
            try { SetGroupArrival.Invoke(bu, new object[] { index, all }); }
            finally { Mp.Applying = false; }
            if (entry >= 0 && bu.entrypoints != null && entry < bu.entrypoints.Length) bu.entrypoints[entry].owner = reg.alliance;
            Arrivals++;
        }

        // ---- army orders and the outcome ----------------------------------------------------------------

        public static void SendArmyOrder(ArmyOrder order)
        {
            if (!BattleRules.LocalCommands) { SpectatorBlocked(); return; }
            SendEvent(EvArmyOrder, w => w.Write((byte)order));
            Session.Notice(order == ArmyOrder.Surrender ? "Surrender sent to " + Session.RemoteName + "'s game."
                : "Retreat ordered. Your army falls back when the retreat timer runs out.");
        }

        /// <summary>Host: the guest's army retreats, withdraws or surrenders, the way an AI army does.</summary>
        private static void OnGuestArmyOrder(ArmyOrder order)
        {
            var bu = GameApi.BU;
            if (bu == null || !BattleRules.GuestCommands) return;
            switch (order)
            {
                case ArmyOrder.Retreat:
                    if (bu.eodcycle > 0) bu.SetEndOfBattle(2);
                    else
                    {
                        bu.SetEndOfBattle(12);
                        GameApi.TP?.SetRetreatTimer(Mp.GuestAlliance);
                    }
                    Session.Notice(Session.RemoteName + "'s army is retreating.");
                    break;
                case ArmyOrder.NightWithdraw:
                    bu.SetEndOfBattle(2);
                    Session.Notice(Session.RemoteName + "'s army withdrew under cover of darkness.");
                    break;
                case ArmyOrder.Surrender:
                    bu.SetEndOfBattle(6);
                    Session.Notice(Session.RemoteName + " surrendered.");
                    break;
            }
        }

        private static int _shownEndBattle = -1;
        private static bool _shownRetreat;

        /// <summary>What the host's battle state means from the guest's side (the end-of-battle panel and codes are per player).</summary>
        private static int GuestView(int host)
        {
            switch (host)
            {
                case 0: return 1;   // host won: defeat
                case 1: return 0;   // host lost: victory
                case 2: return 7;   // we withdrew at night
                case 4: return 2;   // host withdrew at night
                case 6: return 1;   // we surrendered
                case 7: return 13;  // host's army withdrew
                case 8: return 12;  // host's army is retreating
                case 12: return 8;  // we are retreating
                case 13: return 7;  // our army withdrew
                default: return int.MinValue; // host-only prompts (3, 5, 10) and quitting (11)
            }
        }

        private static void GuestOutcome()
        {
            var bu = GameApi.BU;
            if (bu == null) return;
            int host = BattleState.HostEndBattle;
            int view = host < 0 ? -1 : GuestView(host);
            if (view != int.MinValue && view != _shownEndBattle)
            {
                if (view >= 0 || _shownEndBattle >= 0)
                {
                    Mp.Applying = true;
                    try { bu.SetEndOfBattle(view); }
                    finally { Mp.Applying = false; }
                    if (view == 0 || view == 1 || view == 2 || view == 7 || view == 13)
                        Session.Notice("The battle is over. Back to the campaign when " + Session.RemoteName + " leaves the battlefield.");
                }
                _shownEndBattle = view;
            }
            // the host's retreat countdown, shown on the guest's time panel
            var tp = GameApi.TP;
            if (tp != null && BattleState.HostAllianceRetreating >= 0 && !_shownRetreat && tp.retreating < 0f)
            {
                Mp.Applying = true;
                try { tp.SetRetreatTimer(BattleState.HostAllianceRetreating); }
                finally { Mp.Applying = false; }
                _shownRetreat = true;
            }
        }

        // ---- guest: time and the night ----------------------------------------------------------------

        /// <summary>
        /// The host's night skips straight to the next morning's start hour. Following that by running faster
        /// would take forever, so a guest that's far off jumps to the host's time.
        /// </summary>
        private static void GuestClockJump()
        {
            if (ClockSync.HostHoursAt <= 0f) return;
            double hps = ClockSync.SharedPaused ? 0 : ClockSync.HoursPerSecond(ClockSync.SharedSpeed);
            if (System.Math.Abs(ClockSync.DriftHours) < System.Math.Max(0.25, hps * 0.5)) return;
            double target = ClockSync.HostHours + (Time.unscaledTime - ClockSync.HostHoursAt + ClockSync.RttSeconds * 0.5) * hps;
            var bu = GameApi.BU;
            var u = bu?.uniStormSystem;
            if (u == null) return;
            var date = new System.DateTime(1860, 1, 1).AddHours(target);
            float h = (float)date.TimeOfDay.TotalHours;
            u.yearCounter = date.Year;
            u.monthCounter = date.Month;
            u.dayCounter = date.Day;
            u.startTime = h / 24f;
            u.Hour = h;
            u.hourCounter = (int)h;
            u.minuteCounter = (int)((h - (int)h) * 60f);
            bu.SetTimeVariables();
            ClockJumps++;
        }

        public static int ClockJumps;
        private static int _lastHostPrompt = -1, _lastMyPrompt = -1;

        /// <summary>
        /// Guest: shows the host's end-of-day and resupply prompts. Whoever presses Continue first moves the
        /// night along for both (the host's game is the one waiting for it).
        /// </summary>
        private static void GuestPrompts()
        {
            var ph = GameApi.PH;
            var bui = GameApi.BUI;
            if (ph == null || bui == null) return;
            int host = BattleState.HostPrompt, mine = ph.prompt_panelopen;
            bool Night(int p) => p >= 0 && (p == ph.prompt_endofday || p == ph.prompt_resupplied);
            if (host != _lastHostPrompt)
            {
                Mp.Applying = true;
                try
                {
                    if (host == ph.prompt_endofday && mine != host) bui.SetActiveEndOfDayPhase(active: true);
                    else if (host == ph.prompt_resupplied && mine != host) bui.SetActiveTroopsResupplied(active: true);
                    else if (Night(mine) && host != mine) ph.HidePanel(ph.panel, ph.panel_prompt, markedtodestroy: false, hideimmediately: false, resetbuttonstates: false);
                }
                finally { Mp.Applying = false; }
                _lastHostPrompt = host;
                _lastMyPrompt = ph.prompt_panelopen;
                return;
            }
            if (Night(_lastMyPrompt) && mine != _lastMyPrompt && host == _lastMyPrompt)
            {
                int p = _lastMyPrompt;
                SendEvent(EvPromptContinue, w => w.Write((sbyte)p));
            }
            _lastMyPrompt = mine;
        }

        private static void OnPromptContinue(BinaryReader rd)
        {
            int p = rd.ReadSByte();
            var ph = GameApi.PH;
            if (ph == null || ph.prompt_panelopen != p) return;
            ph.HidePanel(ph.panel, ph.panel_prompt, markedtodestroy: false, hideimmediately: false, resetbuttonstates: false);
            Session.Notice(Session.RemoteName + " pressed Continue.");
        }

        private static void Begin()
        {
            _started = true;
            RebuildRegistry();
            BattleOrders.Baseline();
            _wasDeploying = Deploying;
            if (_wasDeploying) StartDeploymentRound();
            if (Mp.Role == Role.Host)
            {
                // battles run at battle speeds; start paused for deployment
                ClockSync.SharedSpeed = 1f;
                ClockSync.SetShared(true, 1f);
            }
            Plugin.Log.LogInfo($"battle sync started: {LocalList.Count} units ({DuplicateIds} duplicate ids), host commands {BattleRules.HostCommands}, guest commands {BattleRules.GuestCommands}");
        }

        // ---- unit registry ----------------------------------------------------------------------------

        private static void RebuildRegistry()
        {
            _nextRegistry = Time.unscaledTime + 3f;
            Local.Clear();
            LocalList.Clear();
            DuplicateIds = 0;
            var root = GameObject.Find("Battlemap/Units");
            var all = root != null ? root.GetComponentsInChildren<Regiment>(true) : Object.FindObjectsOfType<Regiment>();
            foreach (var r in all)
            {
                if (r == null) continue;
                // parents come first in the hierarchy; a later copy of the same id is a runtime detachment
                if (Local.ContainsKey(r.unitid)) { DuplicateIds++; continue; }
                Local[r.unitid] = r;
                LocalList.Add(r);
            }
            if (Mp.Role == Role.Host)
            {
                ByNet.Clear();
                NetList.Clear();
                foreach (var r in LocalList) { ByNet[r.unitid] = r; NetList.Add(r); }
                SendRoster();
            }
            else MatchRoster();
        }

        /// <summary>Host: tells the guest which id each unit goes by (new units only).</summary>
        private static void SendRoster()
        {
            var fresh = new List<Regiment>();
            foreach (var r in LocalList)
                if (RosterSent.Add(r.unitid)) fresh.Add(r);
            for (int off = 0; off < fresh.Count; off += 150)
            {
                int from = off, to = Mathf.Min(fresh.Count, off + 150);
                SendEvent(EvRoster, w =>
                {
                    w.Write((ushort)(to - from));
                    for (int i = from; i < to; i++)
                    {
                        var r = fresh[i];
                        w.Write(r.unitid);
                        w.Write((short)r.alliance);
                        w.Write((short)r.unittyp);
                        w.WriteStr(r.name);
                        w.Write(r.GetPosition());
                    }
                });
            }
        }

        private static void OnRoster(BinaryReader rd)
        {
            int n = rd.ReadUInt16();
            for (int i = 0; i < n; i++)
                Unmatched.Add(new RosterEntry { Id = rd.ReadInt32(), Alliance = rd.ReadInt16(), Typ = rd.ReadInt16(), Name = rd.ReadString(), Pos = rd.ReadVector3() });
            if (_started) MatchRoster();
        }

        /// <summary>Guest: pairs the host's roster with this game's units.</summary>
        private static void MatchRoster()
        {
            if (Unmatched.Count == 0) return;
            for (int i = Unmatched.Count - 1; i >= 0; i--)
            {
                var e = Unmatched[i];
                if (ByNet.ContainsKey(e.Id)) { Unmatched.RemoveAt(i); continue; }
                // same side, type and name; the nearest one if names repeat. Failing that, whatever stands on the spot.
                Regiment best = null, spot = null;
                float bestD = float.MaxValue, spotD = 0.5f;
                foreach (var r in LocalList)
                {
                    if (r.alliance != e.Alliance || r.unittyp != e.Typ || NetOfLocal.ContainsKey(r)) continue;
                    float d = Tools.GetXZDistance(r.GetPosition(), e.Pos);
                    if (r.name == e.Name) { if (d < bestD) { bestD = d; best = r; } }
                    else if (d < spotD) { spotD = d; spot = r; }
                }
                if (best == null) best = spot;
                if (best == null) continue;
                ByNet[e.Id] = best;
                NetOfLocal[best] = e.Id;
                NetList.Add(best);
                Unmatched.RemoveAt(i);
                RosterMatched++;
            }
            RosterUnmatched = Unmatched.Count;
        }

        /// <summary>The units both games know about.</summary>
        public static IEnumerable<Regiment> AllUnits()
        {
            for (int i = 0; i < NetList.Count; i++)
                if (NetList[i] != null) yield return NetList[i];
        }

        /// <summary>A unit by its network id (the host's unit id).</summary>
        public static Regiment Find(int id) => ByNet.TryGetValue(id, out var r) && r != null ? r : null;

        /// <summary>The network id of a local unit, -1 if the other game doesn't know it.</summary>
        public static int NetId(Regiment r)
        {
            if (r == null) return -1;
            if (Mp.Role == Role.Host) return r.unitid;
            return NetOfLocal.TryGetValue(r, out int id) ? id : -1;
        }

        public static void MarkOwnOrder(Regiment r)
        {
            if (r.alliance != Mp.GuestAlliance || !BattleRules.GuestCommands) return;
            BattleOrders.MarkDirty(r);
            // in deployment, our player's moves place the unit; from now on the host follows us for it
            if (Deploying)
            {
                int id = NetId(r);
                if (id >= 0) Placed.Add(id);
            }
        }

        /// <summary>The battle AI that runs (or helps) the given alliance's side.</summary>
        public static AIBattle AiFor(int alliance)
        {
            var bu = GameApi.BU;
            if (bu?.aibattle == null || bu.alliance == null) return null;
            for (int i = 0; i < 2 && i < bu.aibattle.Length; i++)
            {
                var ai = bu.aibattle[i];
                if (ai != null && ai.sideofai >= 0 && ai.sideofai < bu.alliance.Length && bu.alliance[ai.sideofai] == alliance) return ai;
            }
            return null;
        }

        // ---- messages ---------------------------------------------------------------------------------

        public static void Handle(MsgType type, BinaryReader r)
        {
            if (type == MsgType.BattleEvent)
            {
                byte code = r.ReadByte();
                // the roster can arrive before this game is ready; keep it
                if (code == EvRoster) { if (Mp.Role == Role.Guest) OnRoster(r); return; }
                if (_started && BattleLink.BattleSceneReady) OnEvent(code, r);
                return;
            }
            if (!_started || !BattleLink.BattleSceneReady) return;
            switch (type)
            {
                case MsgType.BattleState:
                    if (Mp.Role == Role.Guest) BattleState.OnState(r);
                    break;
                case MsgType.BattleOrder:
                    if (Mp.Role == Role.Host) BattleOrders.OnGuestOrder(r);
                    break;
                case MsgType.BattleOrderState:
                    if (Mp.Role == Role.Guest) BattleOrders.OnHostState(r);
                    break;
            }
        }

        public static void SendEvent(byte code, System.Action<BinaryWriter> body = null) =>
            Session.Send(MsgType.BattleEvent, Wire.Build(w => { w.Write(code); body?.Invoke(w); }));

        private static void OnEvent(byte code, BinaryReader rd)
        {
            bool host = Mp.Role == Role.Host;
            switch (code)
            {
                case EvDeployReady:
                    if (!host) return;
                    _remoteDeployReady = true;
                    _remotePreferredSpeed = rd.ReadSingle();
                    Session.Notice(Session.RemoteName + " is ready to start the battle.");
                    TryEndDeployment();
                    break;
                case EvDeployEnd:
                    if (host) return;
                    BattleState.HostDeploying = false;
                    EndDeploymentLocally();
                    break;
                case EvDeployPlace:
                    if (host) OnGuestPlacement(rd);
                    break;
                case EvTakeCommand:
                    if (host) TakeCommand(Mp.GuestAlliance);
                    break;
                case EvTarget:
                    if (host) OnGuestTarget(rd);
                    break;
                case EvCharge:
                    if (host) OnGuestCharge(rd);
                    break;
                case EvEnterBuilding:
                    if (host) OnGuestEnterBuilding(rd);
                    break;
                case EvPromptContinue:
                    if (host) OnPromptContinue(rd);
                    break;
                case EvArrival:
                    if (!host) OnArrival(rd);
                    break;
                case EvArrivalTimes:
                    if (!host) OnArrivalTimes(rd);
                    break;
                case EvPontoons:
                    if (!host) BattleEngineering.OnPontoons(rd);
                    break;
                case EvPontoonOrder:
                    if (host) BattleEngineering.OnGuestPontoon(rd);
                    break;
                case EvObstacles:
                    if (!host) BattleEngineering.OnObstacles(rd);
                    break;
                case EvObstacleNew:
                    if (host) BattleEngineering.OnGuestLine(rd);
                    break;
                case EvCeaseFire:
                    BattleCeaseFire.OnProposal();
                    break;
                case EvCeaseFireAnswer:
                    BattleCeaseFire.OnAnswer(rd);
                    break;
                case EvArmyOrder:
                    if (host) OnGuestArmyOrder((ArmyOrder)rd.ReadByte());
                    break;
            }
        }

        // ---- deployment ---------------------------------------------------------------------------------

        private static readonly HashSet<int> Placed = new HashSet<int>();

        /// <summary>Guest: units its player has moved during this deployment.</summary>
        public static bool PlacedLocally(int id) => Placed.Contains(id);

        /// <summary>Guest: the host put one of our units somewhere during deployment; that's not a move to report back.</summary>
        public static void NotePlacement(Regiment r)
        {
            int id = NetId(r);
            if (id >= 0) SentPlacement[id] = new KeyValuePair<Vector3, float>(r.GetPosition(), r.transform.eulerAngles.y);
        }

        private static void StartDeploymentRound()
        {
            _localDeployReady = _remoteDeployReady = false;
            Placed.Clear();
            SentPlacement.Clear();
            foreach (var r in AllUnits()) SentPlacement[NetId(r)] = new KeyValuePair<Vector3, float>(r.GetPosition(), r.transform.eulerAngles.y);
            Session.Notice(BattleRules.LocalCommands
                ? "Deployment: place your army, then press play to say you're ready. The battle starts when both sides are ready."
                : "Deployment: the AI deploys your army.");
        }

        /// <summary>The local player ended deployment (play button, speed key, pause key).</summary>
        public static void LocalDeployReady()
        {
            if (_localDeployReady) return;
            _localDeployReady = true;
            if (Mp.Role == Role.Host)
            {
                TryEndDeployment();
                if (Deploying && BattleRules.GuestCommands) Session.Notice("Waiting for " + Session.RemoteName + " to finish deploying...");
            }
            else
            {
                float speed = PreferredSpeed;
                SendEvent(EvDeployReady, w => w.Write(speed));
                Session.Notice("Ready. Waiting for " + Session.RemoteName + " to finish deploying...");
            }
        }

        private static void HostDeployment()
        {
            bool deploying = Deploying;
            if (deploying && !_wasDeploying) StartDeploymentRound();
            _wasDeploying = deploying;
            if (deploying) TryEndDeployment();
        }

        private static void TryEndDeployment()
        {
            if (Mp.Role != Role.Host || !Deploying || !BattleLink.IsLive) return;
            bool hostReady = _localDeployReady || !BattleRules.HostCommands;
            bool guestReady = _remoteDeployReady || !BattleRules.GuestCommands;
            if (!hostReady || !guestReady) return;
            SendEvent(EvDeployEnd);
            EndDeploymentLocally();
            // start at the faster of the speeds the commanding players picked
            float speed = Mathf.Max(BattleRules.HostCommands ? PreferredSpeed : 1f, BattleRules.GuestCommands ? _remotePreferredSpeed : 1f, 1f);
            ClockSync.SetShared(false, speed);
            _remotePreferredSpeed = 1f;
            Session.SendNotice("Both sides are deployed. The battle begins!");
        }

        private static void EndDeploymentLocally()
        {
            _localDeployReady = _remoteDeployReady = false;
            var bui = GameApi.BUI;
            if (bui == null || !bui.deploymentmodeactive) return;
            Mp.Applying = true;
            try { bui.SetActiveDeploymentPhase(active: false); }
            finally { Mp.Applying = false; }
            _wasDeploying = false;
        }

        private static void GuestDeployment()
        {
            bool deploying = Deploying;
            // a new morning on the host: deploy again
            if (BattleState.HostDeployStarted)
            {
                BattleState.HostDeployStarted = false;
                if (!deploying)
                {
                    Mp.Applying = true;
                    try { GameApi.BUI.SetActiveDeploymentPhase(active: true, showsupplybydefault: false); }
                    finally { Mp.Applying = false; }
                    deploying = Deploying;
                }
            }
            // the host is past deployment (a missed "battle begins"): follow it
            if (deploying && !BattleState.HostDeploying && BattleState.MotionReceived > 0 && Time.unscaledTime - BattleState.HostDeployingAt > 3f)
            {
                EndDeploymentLocally();
                deploying = Deploying;
            }
            if (deploying && !_wasDeploying) StartDeploymentRound();
            _wasDeploying = deploying;
            if (!deploying || !BattleRules.GuestCommands || Time.unscaledTime < _nextPlacement) return;
            _nextPlacement = Time.unscaledTime + 0.2f;
            // the guest places its own army (units its player moved during this deployment); tell the host where
            foreach (int id in Placed)
            {
                var r = Find(id);
                if (r == null || r.alliance != Mp.GuestAlliance || !BattleState.IsReady(r) || !r.gameObject.activeInHierarchy) continue;
                var pos = r.GetPosition();
                float rot = r.transform.eulerAngles.y;
                if (SentPlacement.TryGetValue(id, out var last)
                    && (last.Key - pos).sqrMagnitude < 0.09f && Mathf.Abs(Mathf.DeltaAngle(last.Value, rot)) < 2f)
                    continue;
                SentPlacement[id] = new KeyValuePair<Vector3, float>(pos, rot);
                SendEvent(EvDeployPlace, w => { w.Write(id); w.Write(pos); w.Write(rot); });
            }
        }

        private static void OnGuestPlacement(BinaryReader rd)
        {
            int id = rd.ReadInt32();
            var pos = rd.ReadVector3();
            float rot = rd.ReadSingle();
            var r = Find(id);
            if (r == null || r.alliance != Mp.GuestAlliance || !BattleRules.GuestCommands || !Deploying) return;
            Mp.Applying = true;
            try { BattleOrderSnap.PlaceKeepingJob(r, pos, rot, usecoverplacement: true, ignoredeploymentzone: false); }
            finally { Mp.Applying = false; }
            BattleOrders.Remember(r);
        }

        // ---- taking command ---------------------------------------------------------------------------------

        public static void RequestTakeCommand()
        {
            if (BattleRules.LocalCommands) return;
            if (Mp.Role == Role.Host) TakeCommand(Mp.HostAlliance);
            else
            {
                SendEvent(EvTakeCommand);
                Session.Notice("Asking the host to hand you your army...");
            }
        }

        /// <summary>Host: the AI stops running this alliance's army and its player commands it from now on.</summary>
        private static void TakeCommand(int alliance)
        {
            if (BattleRules.Commanding(alliance)) return;
            BattleRules.SetCommanding(alliance, true);
            var ai = AiFor(alliance);
            int released = 0;
            if (ai != null)
            {
                ai.isplayeraiorfeud = 1;
                foreach (var r in AllUnits())
                {
                    if (r.alliance != alliance) continue;
                    ai.RemoveUnitFromPool(r);
                    r.aiassignedref = null;
                    if (r.ai_stance != 0 || r.ai_stanceordered != 0)
                    {
                        r.ai_stanceordered = 0;
                        r.SetAIStance(0);
                    }
                    released++;
                }
            }
            string who = alliance == Mp.HostAlliance ? Plugin.CfgPlayerName.Value : Session.RemoteName;
            Plugin.Log.LogInfo($"take command: alliance {alliance}, {released} units released from the AI");
            Session.SendNotice($"{who} took command of the {Session.AllianceName(alliance)} army.");
        }

        public static void SpectatorBlocked()
        {
            if (Time.unscaledTime < _lastSpectatorNotice + 5f) return;
            _lastSpectatorNotice = Time.unscaledTime;
            Session.Notice("You're spectating: the AI commands your army. Press \"Take command\" in the GT Live window (F8) to take over.");
        }

        // ---- orders only the host can carry out ----------------------------------------------------------

        private static int IdOf(GameObject go)
        {
            if (go == null) return -1;
            var r = go.GetComponent<Regiment>();
            return r != null ? NetId(r) : -1;
        }

        public static void SendTarget(Regiment r, GameObject target, float shorter, int type)
        {
            int id = NetId(r), tid = IdOf(target);
            if (tid < 0) return;
            SendEvent(EvTarget, w => { w.Write(id); w.Write(tid); w.Write(shorter); w.Write(type); });
        }

        public static void SendCharge(Regiment r, GameObject target, bool usechargeorder)
        {
            int id = NetId(r), tid = IdOf(target);
            SendEvent(EvCharge, w => { w.Write(id); w.Write(tid); w.Write(usechargeorder); });
        }

        public static void SendEnterBuilding(Regiment r, GameObject building)
        {
            if (building == null) return;
            int id = NetId(r);
            var pos = building.transform.position;
            SendEvent(EvEnterBuilding, w => { w.Write(id); w.Write(pos); });
        }

        private static Regiment GuestUnit(int id)
        {
            var r = Find(id);
            return r != null && r.alliance == Mp.GuestAlliance && BattleRules.GuestCommands ? r : null;
        }

        private static void OnGuestTarget(BinaryReader rd)
        {
            var r = GuestUnit(rd.ReadInt32());
            var t = Find(rd.ReadInt32());
            float shorter = rd.ReadSingle();
            int type = rd.ReadInt32();
            if (r == null || t == null) return;
            Mp.Applying = true;
            try { r.AssignTarget(t.gameObject, uiactive: true, shorter, type); }
            finally { Mp.Applying = false; }
        }

        private static void OnGuestCharge(BinaryReader rd)
        {
            var r = GuestUnit(rd.ReadInt32());
            int tid = rd.ReadInt32();
            bool use = rd.ReadBoolean();
            if (r == null) return;
            var t = tid >= 0 ? Find(tid) : null;
            Mp.Applying = true;
            try { r.AssignChargeTarget(t != null ? t.gameObject : null, use); }
            finally { Mp.Applying = false; }
        }

        private static readonly MethodInfo EnterBuilding = AccessTools.Method(typeof(BattleUI), "AssignUnitToEnterBuilding");

        private static void OnGuestEnterBuilding(BinaryReader rd)
        {
            var r = GuestUnit(rd.ReadInt32());
            var pos = rd.ReadVector3();
            if (r == null || GameApi.BUI == null || EnterBuilding == null) return;
            Building best = null;
            float bestD = 4f;
            foreach (var b in Object.FindObjectsOfType<Building>())
            {
                float d = (b.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = b; }
            }
            if (best == null) return;
            Mp.Applying = true;
            try { EnterBuilding.Invoke(GameApi.BUI, new object[] { r.gameObject, best.gameObject }); }
            finally { Mp.Applying = false; }
        }

        // ---- overlay ------------------------------------------------------------------------------------------

        public static void DrawOverlay(GUIStyle status, GUIStyle small)
        {
            if (!_started) { GUILayout.Label("Battle: setting up...", status); return; }
            string remote = Session.RemoteName;
            GUILayout.Label(BattleRules.LocalCommands
                ? "Battle: you command your army."
                : "Battle: you are spectating. The AI commands your army and you only see what it sees.", status);
            GUILayout.Label(BattleRules.RemoteCommands ? remote + " commands theirs." : remote + " is spectating; the AI commands their army.", small);
            if (Deploying)
            {
                bool host = Mp.Role == Role.Host;
                string me = _localDeployReady || !BattleRules.LocalCommands ? "ready" : "deploying";
                string them = host ? (_remoteDeployReady || !BattleRules.GuestCommands ? "ready" : "deploying") : "-";
                GUILayout.Label(host ? $"Deployment: you {me}, {remote} {them}." : $"Deployment: you {me}. Press play when your army is placed.", small);
            }
            BattleCeaseFire.DrawOverlay(status);
            if (!BattleRules.LocalCommands && GUILayout.Button("Take command of my army")) RequestTakeCommand();
            if (Mp.Role == Role.Guest)
                GUILayout.Label($"Units {NetList.Count} (unmatched {RosterUnmatched})  drift avg {BattleState.AvgDrift:0.0} max {BattleState.MaxDrift:0.0}  corrections {BattleState.Corrections}  orders sent {BattleOrders.Sent} recv {BattleOrders.Received}", small);
            else
                GUILayout.Label($"Units {NetList.Count}  orders broadcast {BattleOrders.Sent}, from guest {BattleOrders.Received}", small);
        }
    }
}
