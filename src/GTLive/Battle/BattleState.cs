using System.Collections.Generic;
using System.IO;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// Host -> guest: where every unit is and what shape it's in.
    ///   Motion (5 Hz, units that moved): position, facing, rout/retreat flags, how many waypoints are left.
    ///   Stats (1 Hz, units that changed): strength and losses, morale, fatigue, ammunition, current target.
    ///   Each message starts with the battle-wide state (day, end-of-day phase, deployment, objectives, commanders).
    /// The guest moves its copies along the same routes itself (BattleOrders); motion only corrects drift.
    /// </summary>
    public static class BattleState
    {
        private const byte KindMotion = 1, KindStats = 2;

        private class MotionRec
        {
            public Vector3 Pos;
            public float Rot, Time;
            public byte Flags, Waypoints;
        }

        private class StatsRec
        {
            public int Strength, Sick, Surrender, Target, Guns;
            public float Morale, Fatigue, Ammo, Time;
        }

        private static readonly Dictionary<int, MotionRec> SentMotion = new Dictionary<int, MotionRec>();
        private static readonly Dictionary<int, StatsRec> SentStats = new Dictionary<int, StatsRec>();
        private static float _nextMotion, _nextStats;
        private static readonly AccessTools.FieldRef<Regiment, bool> Initialized = AccessTools.FieldRefAccess<Regiment, bool>("initialized");

        // guest side
        public static int Corrections, MotionReceived, StatsReceived, CasualtyEvents, Deactivated, InactiveMismatch;
        private static readonly HashSet<int> SeenActive = new HashSet<int>();
        public static float AvgDrift, MaxDrift;
        public static int HostEodCycle, HostEndBattle = -1, HostPrompt = -1, HostAllianceRetreating = -1;
        public static bool HostDeploying, HostDeployStarted;
        public static float HostDeployingAt;

        public static void Reset()
        {
            SentMotion.Clear();
            SentStats.Clear();
            Corrections = MotionReceived = StatsReceived = CasualtyEvents = Deactivated = InactiveMismatch = 0;
            SeenActive.Clear();
            HostPrompt = HostAllianceRetreating = -1;
            AvgDrift = MaxDrift = 0f;
            HostEodCycle = 0;
            HostEndBattle = -1;
            HostDeploying = HostDeployStarted = false;
            HostDeployingAt = 0f;
        }

        public static bool IsReady(Regiment r) => r != null && Initialized(r);

        // ---- flags ----------------------------------------------------------------------------------------

        private const byte F_Active = 1, F_Routed = 2, F_MarkedRout = 4, F_Retreat = 8, F_Melee = 16, F_Moving = 32;

        private static byte FlagsOf(Regiment r) => (byte)(
            (r.gameObject.activeInHierarchy ? F_Active : 0) | (r.isrouted ? F_Routed : 0) | (r.markedforrout ? F_MarkedRout : 0)
            | (r.onretreat ? F_Retreat : 0) | (r.inmelee > 0f ? F_Melee : 0) | (r.regimentpaths > 0 ? F_Moving : 0));

        // ---- host -----------------------------------------------------------------------------------------

        public static void HostTick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextMotion)
            {
                _nextMotion = now + 0.2f;
                SendMotion(now);
            }
            if (now >= _nextStats)
            {
                _nextStats = now + 1f;
                SendStats(now);
            }
        }

        private static void WriteHeader(BinaryWriter w)
        {
            var bu = GameApi.BU;
            w.Write((sbyte)bu.eodcycle);
            w.Write((short)bu.battlepasseddays);
            w.Write(GameApi.BUI != null && GameApi.BUI.deploymentmodeactive);
            w.Write((sbyte)bu.endbattle);
            w.Write(BattleRules.HostCommands);
            w.Write(BattleRules.GuestCommands);
            var ph = GameApi.PH;
            w.Write((sbyte)(ph != null ? ph.prompt_panelopen : -1));
            var u = bu.uniStormSystem;
            w.Write((sbyte)(u != null ? u.weatherForecaster : -1));
            var bfs = GameApi.BFS;
            w.Write((sbyte)(bfs != null ? bfs.currentweather : -1));
            var tp = GameApi.TP;
            w.Write((sbyte)(tp != null ? tp.allianceretreating : -1));
            var obj = BattleUnits.objectives;
            int n = obj == null ? 0 : Mathf.Min(obj.Length, 255);
            w.Write((byte)n);
            for (int i = 0; i < n; i++) w.Write((sbyte)(obj[i] != null ? obj[i].owner : -1));
        }

        private static void SendMotion(float now)
        {
            var list = new List<Regiment>();
            foreach (var r in BattleSync.AllUnits())
            {
                if (!IsReady(r)) continue;
                var pos = r.GetPosition();
                float rot = r.transform.eulerAngles.y;
                byte flags = FlagsOf(r);
                byte wps = (byte)Mathf.Clamp(r.regimentpaths - BattleOrderSnap.CurrentPath(r), 0, 255);
                if (SentMotion.TryGetValue(r.unitid, out var m)
                    && (pos - m.Pos).sqrMagnitude < 0.09f && Mathf.Abs(Mathf.DeltaAngle(rot, m.Rot)) < 2f
                    && flags == m.Flags && wps == m.Waypoints && now - m.Time < 3f)
                    continue;
                SentMotion[r.unitid] = new MotionRec { Pos = pos, Rot = rot, Flags = flags, Waypoints = wps, Time = now };
                list.Add(r);
            }
            // always send something so the guest gets the header (deployment, day, objectives)
            for (int off = 0; off < list.Count || off == 0; off += 400)
            {
                int from = off, to = Mathf.Min(list.Count, off + 400);
                Session.Send(MsgType.BattleState, Wire.Build(w =>
                {
                    w.Write(KindMotion);
                    WriteHeader(w);
                    w.Write((ushort)(to - from));
                    for (int i = from; i < to; i++)
                    {
                        var r = list[i];
                        var mr = SentMotion[r.unitid];
                        w.Write(r.unitid);
                        w.Write(mr.Pos);
                        w.Write(mr.Rot);
                        w.Write(mr.Flags);
                        w.Write(mr.Waypoints);
                    }
                }));
            }
        }

        private static float AmmoOf(Regiment r)
        {
            float a = 0f;
            if (r.ammo != null) for (int i = 0; i < r.ammo.Length; i++) a += r.ammo[i];
            if (r.supplyinstock != null) for (int i = 0; i < r.supplyinstock.Length; i++) a += r.supplyinstock[i] * 0.001f;
            return a;
        }

        private static int TargetOf(Regiment r)
        {
            var t = r.targetedenemyunit;
            if (t == null) return -1;
            var tr = t.GetComponent<Regiment>();
            return tr != null ? tr.unitid : -1;
        }

        private static void SendStats(float now)
        {
            var list = new List<Regiment>();
            foreach (var r in BattleSync.AllUnits())
            {
                if (!IsReady(r)) continue;
                var s = new StatsRec
                {
                    Strength = r.strength, Sick = r.sick, Surrender = r.surrenderstate, Target = TargetOf(r), Guns = r.guns,
                    Morale = r.morale, Fatigue = r.fatigue, Ammo = AmmoOf(r), Time = now,
                };
                if (SentStats.TryGetValue(r.unitid, out var o)
                    && o.Strength == s.Strength && o.Sick == s.Sick && o.Surrender == s.Surrender && o.Target == s.Target && o.Guns == s.Guns
                    && Mathf.Abs(o.Morale - s.Morale) < 0.01f && Mathf.Abs(o.Fatigue - s.Fatigue) < 0.01f && Mathf.Abs(o.Ammo - s.Ammo) < 0.05f
                    && now - o.Time < 6f)
                    continue;
                SentStats[r.unitid] = s;
                list.Add(r);
            }
            for (int off = 0; off < list.Count; off += 200)
            {
                int from = off, to = Mathf.Min(list.Count, off + 200);
                Session.Send(MsgType.BattleState, Wire.Build(w =>
                {
                    w.Write(KindStats);
                    WriteHeader(w);
                    w.Write((ushort)(to - from));
                    for (int i = from; i < to; i++)
                    {
                        var r = list[i];
                        w.Write(r.unitid);
                        w.Write(r.strength);
                        w.Write(r.sick);
                        for (int k = 0; k < 6; k++) w.Write(r.losses != null && k < r.losses.Length ? r.losses[k] : 0);
                        w.Write(r.morale);
                        w.Write(r.fatigue);
                        w.Write(r.cohesion);
                        w.Write(r.experience);
                        w.Write(r.inmelee);
                        for (int k = 0; k < 3; k++) w.Write(r.ammo != null && k < r.ammo.Length ? r.ammo[k] : 0f);
                        for (int k = 0; k < 4; k++) w.Write(r.supplyinstock != null && k < r.supplyinstock.Length ? r.supplyinstock[k] : 0f);
                        w.Write((sbyte)r.surrenderstate);
                        w.Write(TargetOf(r));
                        w.Write((short)r.guns);
                    }
                }));
            }
        }

        // ---- guest ----------------------------------------------------------------------------------------

        public static void OnState(BinaryReader rd)
        {
            byte kind = rd.ReadByte();
            ReadHeader(rd);
            int n = rd.ReadUInt16();
            if (kind == KindMotion) ApplyMotion(rd, n);
            else if (kind == KindStats) ApplyStats(rd, n);
        }

        private static void ReadHeader(BinaryReader rd)
        {
            var bu = GameApi.BU;
            HostEodCycle = rd.ReadSByte();
            int days = rd.ReadInt16();
            bool deploying = rd.ReadBoolean();
            if (deploying && !HostDeploying) HostDeployStarted = true;
            if (deploying) HostDeployingAt = Time.unscaledTime;
            HostDeploying = deploying;
            int endbattle = rd.ReadSByte();
            bool hostCmd = rd.ReadBoolean(), guestCmd = rd.ReadBoolean();
            HostPrompt = rd.ReadSByte();
            int forecaster = rd.ReadSByte();
            int weather = rd.ReadSByte();
            HostAllianceRetreating = rd.ReadSByte();
            int n = rd.ReadByte();
            var owners = new int[n];
            for (int i = 0; i < n; i++) owners[i] = rd.ReadSByte();

            if (bu == null || bu.IsCampaign) return;
            bu.eodcycle = HostEodCycle;
            bu.battlepasseddays = days;
            // weather decides how far everyone sees; it changes at random and overnight on the host
            if (forecaster >= 0 && bu.uniStormSystem != null) bu.uniStormSystem.weatherForecaster = forecaster;
            var bfs = GameApi.BFS;
            if (weather >= 0 && bfs != null) bfs.currentweather = weather;
            HostEndBattle = endbattle;
            if (hostCmd != BattleRules.HostCommands || guestCmd != BattleRules.GuestCommands)
            {
                BattleRules.HostCommands = hostCmd;
                BattleRules.GuestCommands = guestCmd;
            }
            var obj = BattleUnits.objectives;
            if (obj != null)
                for (int i = 0; i < n && i < obj.Length; i++)
                    if (obj[i] != null) obj[i].owner = owners[i];
        }

        private static bool OwnCommanded(Regiment r) => r.alliance == Mp.GuestAlliance && BattleRules.GuestCommands;

        private static void ApplyMotion(BinaryReader rd, int n)
        {
            MotionReceived++;
            bool deploying = GameApi.BUI != null && GameApi.BUI.deploymentmodeactive;
            float sum = 0f, max = 0f;
            int counted = 0;
            for (int i = 0; i < n; i++)
            {
                int id = rd.ReadInt32();
                var pos = rd.ReadVector3();
                float rot = rd.ReadSingle();
                byte flags = rd.ReadByte();
                int wps = rd.ReadByte();
                var r = BattleSync.Find(id);
                if (!IsReady(r)) continue;
                bool hostActive = (flags & F_Active) != 0;
                if (hostActive) SeenActive.Add(id);
                if (!hostActive)
                {
                    // destroyed, withdrawn or gone on the host (units that never arrived yet are left alone)
                    if (r.gameObject.activeSelf && SeenActive.Contains(id) && r.unittyp <= 13)
                    {
                        Mp.Applying = true;
                        try { r.DeactivateUnit(); }
                        finally { Mp.Applying = false; }
                        Deactivated++;
                    }
                    continue;
                }
                if (!r.gameObject.activeInHierarchy) { InactiveMismatch++; continue; }
                // in deployment the guest places its own army: once the guest moved a unit, the host follows the guest.
                // (Until then the host's placement counts; the guest's game faces its own units from the host's point of view.)
                if (deploying && OwnCommanded(r) && BattleSync.PlacedLocally(id)) continue;

                Mp.Applying = true;
                try
                {
                    ApplyFlags(r, flags);
                    float err = Tools.GetXZDistance(r.GetPosition(), pos);
                    sum += err; counted++;
                    if (err > max) max = err;
                    // the host's word counts for units we don't command; our own may be moving on an order the host hasn't seen yet
                    bool hostMoving = (flags & F_Moving) != 0;
                    bool moving = hostMoving || (r.regimentpaths > 0 && OwnCommanded(r));
                    float limit = deploying ? 0.75f : moving ? 15f : 3f;
                    if (err > limit) Correct(r, pos, rot, wps, moving, deploying);
                    else if (moving && !deploying && err > (OwnCommanded(r) ? 2.5f : 1.5f))
                    {
                        // on the move: close half the gap; the men walk to catch up instead of jumping
                        var d = pos - r.GetPosition();
                        Nudge(r, new Vector3(d.x, 0f, d.z) * 0.5f);
                    }
                    else if (!moving && !r.withinrotationprocess && Mathf.Abs(Mathf.DeltaAngle(r.transform.eulerAngles.y, rot)) > (deploying ? 3f : 15f))
                        r.RotateRegiment(rot);
                    if (deploying && OwnCommanded(r)) BattleSync.NotePlacement(r);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"battle motion {id}: {e.Message}");
                }
                finally { Mp.Applying = false; }
            }
            if (counted > 0)
            {
                AvgDrift = Mathf.Lerp(AvgDrift, sum / counted, 0.3f);
                MaxDrift = max;
            }
        }

        private static readonly AccessTools.FieldRef<Regiment, Vector3[]> DeparentPositions = AccessTools.FieldRefAccess<Regiment, Vector3[]>("deparent_blockposition");
        public static int Nudges;

        /// <summary>
        /// Shifts a marching unit without stopping it. Its company blocks are children of the regiment, except
        /// while the game has them detached for an update (it puts them back from deparent_blockposition).
        /// </summary>
        private static void Nudge(Regiment r, Vector3 d)
        {
            Nudges++;
            r.SetPosition(r.GetPosition() + d);
            var saved = DeparentPositions(r);
            if (saved != null && !r.blocksparented)
                for (int i = 0; i < saved.Length; i++) saved[i] += d;
        }

        /// <summary>Puts a unit where the host has it and sends it on along what's left of its route.</summary>
        private static void Correct(Regiment r, Vector3 pos, float rot, int hostWaypoints, bool moving, bool deploying)
        {
            Corrections++;
            var local = BattleOrderSnap.Capture(r);
            BattleOrders.Authoritative.TryGetValue(BattleSync.NetId(r), out var auth);
            BattleOrderSnap.PlaceKeepingJob(r, pos, rot, usecoverplacement: !moving, ignoredeploymentzone: true);
            if (deploying || hostWaypoints <= 0) { BattleOrders.Remember(r); return; }
            BattleOrderSnap route = null;
            if (local.Waypoints.Count == hostWaypoints) route = local;
            else if (auth != null && auth.Waypoints.Count >= hostWaypoints) route = auth.Tail(hostWaypoints);
            else if (local.Waypoints.Count > 0) route = local;
            route?.ApplyTo(r, forceRoute: true);
            BattleOrders.Remember(r);
        }

        private static void ApplyFlags(Regiment r, byte flags)
        {
            bool routed = (flags & F_Routed) != 0;
            bool marked = (flags & F_MarkedRout) != 0;
            if ((routed || marked) && !r.isrouted && r.unittyp <= 13)
                r.Rout(skipstatsupdate: true);
            else if (!routed && !marked && r.isrouted)
            {
                // rallied on the host
                r.isrouted = false;
                r.markedforrout = false;
            }
            r.onretreat = (flags & F_Retreat) != 0;
            if ((flags & F_Melee) == 0 && r.inmelee > 0f) r.inmelee = 0f;
        }

        private static void ApplyStats(BinaryReader rd, int n)
        {
            StatsReceived++;
            for (int i = 0; i < n; i++)
            {
                int id = rd.ReadInt32();
                int strength = rd.ReadInt32();
                int sick = rd.ReadInt32();
                var losses = new int[6];
                for (int k = 0; k < 6; k++) losses[k] = rd.ReadInt32();
                float morale = rd.ReadSingle(), fatigue = rd.ReadSingle(), cohesion = rd.ReadSingle(), experience = rd.ReadSingle(), inmelee = rd.ReadSingle();
                var ammo = new float[3];
                for (int k = 0; k < 3; k++) ammo[k] = rd.ReadSingle();
                var supply = new float[4];
                for (int k = 0; k < 4; k++) supply[k] = rd.ReadSingle();
                int surrender = rd.ReadSByte();
                int target = rd.ReadInt32();
                int guns = rd.ReadInt16();

                var r = BattleSync.Find(id);
                if (!IsReady(r)) continue;
                Mp.Applying = true;
                try
                {
                    if (strength < r.strength && r.gameObject.activeInHierarchy && r.unittyp <= 13)
                    {
                        // the game's own casualty routine, for the falling men and the thinning ranks
                        CasualtyEvents++;
                        for (int k = 0; k < 8 && r.strength > strength; k++)
                        {
                            int lost = r.SufferCasualties(r.strength - strength, userelativevalues: false, -1, null, null, default, -1, -1, null, showmessage: false);
                            if (lost <= 0) break;
                        }
                    }
                    r.strength = strength;
                    r.sick = sick;
                    if (r.losses != null) for (int k = 0; k < 6 && k < r.losses.Length; k++) r.losses[k] = losses[k];
                    r.morale = morale;
                    r.fatigue = fatigue;
                    r.cohesion = cohesion;
                    r.experience = experience;
                    r.inmelee = inmelee;
                    if (r.ammo != null) for (int k = 0; k < 3 && k < r.ammo.Length; k++) r.ammo[k] = ammo[k];
                    if (r.supplyinstock != null) for (int k = 0; k < 4 && k < r.supplyinstock.Length; k++) r.supplyinstock[k] = supply[k];
                    r.surrenderstate = surrender;
                    r.guns = guns;
                    var t = target >= 0 ? BattleSync.Find(target) : null;
                    r.targetedenemyunit = t != null ? t.gameObject : null;
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"battle stats {id}: {e.Message}");
                }
                finally { Mp.Applying = false; }
            }
        }
    }
}
