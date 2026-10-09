using System.Collections.Generic;
using System.IO;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// What a unit in a 3D battle has been told to do: its remaining waypoints (with their courier status) and its
    /// standing orders (formation, combat behaviour, AI stance, detached, mounted, pace).
    /// </summary>
    public class BattleOrderSnap
    {
        public int UnitId;
        public readonly List<Vector3> Waypoints = new List<Vector3>();
        public readonly List<int> PathStatus = new List<int>();
        public float FinalRotation;
        public int MovementMode, Formation, CombatBehavior, AiStance, Mounted;
        public bool DoubleQuick, Detached;

        public static int CurrentPath(Regiment r)
        {
            if (r.blockobjectcurrentpath != null && r.blockobjectcurrentpath.Length > 0
                && r.blockobjectcurrentpath[0] >= 0 && r.blockobjectcurrentpath[0] < r.regimentpaths)
                return r.blockobjectcurrentpath[0];
            return 0;
        }

        public static BattleOrderSnap Capture(Regiment r)
        {
            var s = new BattleOrderSnap { UnitId = BattleSync.NetId(r) };
            if (r.regimentpath != null)
            {
                for (int k = CurrentPath(r); k < r.regimentpaths && k < r.regimentpath.Length; k++)
                {
                    var p = r.regimentpath[k];
                    if (p == null) continue;
                    var c = p.corners;
                    if (c == null || c.Length == 0) continue;
                    s.Waypoints.Add(c[c.Length - 1]);
                    s.PathStatus.Add(r.pathstatus != null && k < r.pathstatus.Length ? r.pathstatus[k] : 2);
                }
            }
            s.FinalRotation = r.lastwaypointrotation != null && r.lastwaypointrotation.Length > 0
                ? r.lastwaypointrotation[0] : r.transform.eulerAngles.y;
            s.MovementMode = r.movementmode;
            s.DoubleQuick = r.doublequick;
            s.Formation = r.formation;
            s.CombatBehavior = r.combatbehavior;
            s.AiStance = r.ai_stance;
            s.Detached = r.permanentlydetached;
            s.Mounted = r.mounted;
            return s;
        }

        private static bool Near(Vector3 a, Vector3 b) => (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z) < 0.04f;

        public bool SameRoute(BattleOrderSnap o)
        {
            if (Waypoints.Count != o.Waypoints.Count) return false;
            for (int i = 0; i < Waypoints.Count; i++)
                if (!Near(Waypoints[i], o.Waypoints[i])) return false;
            return true;
        }

        public bool SameStatus(BattleOrderSnap o)
        {
            if (PathStatus.Count != o.PathStatus.Count) return false;
            for (int i = 0; i < PathStatus.Count; i++)
                if (PathStatus[i] != o.PathStatus[i]) return false;
            return true;
        }

        public bool SameSettings(BattleOrderSnap o) =>
            MovementMode == o.MovementMode && DoubleQuick == o.DoubleQuick && Formation == o.Formation
            && CombatBehavior == o.CombatBehavior && AiStance == o.AiStance && Detached == o.Detached && Mounted == o.Mounted;

        public bool Same(BattleOrderSnap o) => SameRoute(o) && SameStatus(o) && SameSettings(o);

        /// <summary>True if this route is <paramref name="older"/> with leading waypoints already reached and nothing else changed.</summary>
        public bool IsProgressOf(BattleOrderSnap older)
        {
            int drop = older.Waypoints.Count - Waypoints.Count;
            if (drop <= 0 || !SameSettings(older)) return false;
            for (int i = 0; i < Waypoints.Count; i++)
                if (!Near(Waypoints[i], older.Waypoints[i + drop]) || PathStatus[i] != older.PathStatus[i + drop]) return false;
            return true;
        }

        /// <summary>The last <paramref name="n"/> waypoints of this route (what's left once the others are reached).</summary>
        public BattleOrderSnap Tail(int n)
        {
            var s = (BattleOrderSnap)MemberwiseClone();
            var w = new List<Vector3>(Waypoints);
            var p = new List<int>(PathStatus);
            int drop = Mathf.Clamp(w.Count - n, 0, w.Count);
            w.RemoveRange(0, drop);
            p.RemoveRange(0, drop);
            s.Waypoints.Clear(); s.Waypoints.AddRange(w);
            s.PathStatus.Clear(); s.PathStatus.AddRange(p);
            return s;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(UnitId);
            w.Write((byte)Mathf.Min(Waypoints.Count, 255));
            for (int i = 0; i < Waypoints.Count && i < 255; i++)
            {
                w.Write(Waypoints[i]);
                w.Write((sbyte)PathStatus[i]);
            }
            w.Write(FinalRotation);
            w.Write((sbyte)MovementMode);
            w.Write((sbyte)Formation);
            w.Write((sbyte)CombatBehavior);
            w.Write((sbyte)AiStance);
            w.Write((sbyte)Mounted);
            w.Write((byte)((DoubleQuick ? 1 : 0) | (Detached ? 2 : 0)));
        }

        public static BattleOrderSnap Read(BinaryReader r)
        {
            var s = new BattleOrderSnap { UnitId = r.ReadInt32() };
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                s.Waypoints.Add(r.ReadVector3());
                s.PathStatus.Add(r.ReadSByte());
            }
            s.FinalRotation = r.ReadSingle();
            s.MovementMode = r.ReadSByte();
            s.Formation = r.ReadSByte();
            s.CombatBehavior = r.ReadSByte();
            s.AiStance = r.ReadSByte();
            s.Mounted = r.ReadSByte();
            byte f = r.ReadByte();
            s.DoubleQuick = (f & 1) != 0;
            s.Detached = (f & 2) != 0;
            return s;
        }

        /// <summary>Makes <paramref name="r"/> follow this snapshot. Call with Mp.Applying set.</summary>
        public void ApplyTo(Regiment r, bool forceRoute = false)
        {
            var bu = GameApi.BU;
            // standing orders first: formation changes re-place the unit, routes come after
            if (Formation != r.formation)
            {
                if (r.unittyp <= 13)
                {
                    if (!r.withinrotationprocess)
                    {
                        r.formationordered = Formation;
                        r.ChangeRegimentFormation(GameVars.SetFormationParam(Formation, manualset: true));
                    }
                }
                else
                {
                    r.formation = Formation;
                    r.formationordered = Formation;
                }
            }
            if (CombatBehavior != r.combatbehavior)
            {
                r.combatbehaviorordered = CombatBehavior;
                r.SetCombatBehaviorImmediate(CombatBehavior);
            }
            if (AiStance != r.ai_stance)
            {
                if (Mp.IsHost && bu != null) bu.ChangeStance(r.gameObject, AiStance, immediate: true, overwriteaigroups: false);
                else
                {
                    r.ai_stanceordered = AiStance;
                    r.SetAIStance(AiStance);
                }
            }
            r.permanentlydetached = Detached;
            if (Mounted != r.mounted && !r.spritesstillmounting && bu != null && (r.unittyp == 1 || r.ishorseartillery))
                bu.MountUnits(r, Mounted);

            var cur = Capture(r);
            // a new route stops the unit, and stopping releases it from building a bridge or an obstacle; the game
            // sets the route before assigning the job, but here the route can arrive after it
            var pontoonJob = r.pontoonconstruction;
            var pontoon = pontoonJob != null ? pontoonJob.GetComponent<Pontoons>() : null;
            int pontoonSlot = pontoon != null ? System.Array.IndexOf(pontoon.allocatedreg, r.gameObject) : -1;
            var obstacleJob = r.obstacleconstruction;
            if (forceRoute || !SameRoute(cur))
            {
                if (Waypoints.Count == 0)
                {
                    if (cur.Waypoints.Count > 0)
                        r.StopRegiment(skipfinalrotation: true, manualstop: true, skipmarchcolumnstop: false, skipadvancefallbackstop: false, clearregimentpathsbeforerotation: false, playstopbugle: false);
                }
                else
                {
                    r.immediateunitplacement = false;
                    for (int j = 0; j < Waypoints.Count; j++)
                    {
                        float rot = j == Waypoints.Count - 1 ? FinalRotation : -1f;
                        r.RegimentSetPath(Waypoints[j], newpath: j == 0, skipcover: false, rot, modifylastwaypoint: false, PathStatus[j], -1f, null, autoadvancefallback: false, uimovement: false);
                    }
                }
            }
            else if (!SameStatus(cur) && r.pathstatus != null)
            {
                // same route, a courier arrived: start (or hold) the legs in place
                int start = CurrentPath(r);
                for (int j = 0; j < PathStatus.Count && start + j < r.pathstatus.Length; j++)
                    r.pathstatus[start + j] = PathStatus[j];
            }
            r.doublequick = DoubleQuick;
            if (MovementMode != r.movementmode) r.SetMovementMode(MovementMode);
            KeepJob(r, pontoon, pontoonSlot, pontoonJob, obstacleJob);
        }

        /// <summary>Moves a unit (SetNewPosition stops it) without dropping an engineering job at the spot it ends up.</summary>
        public static void PlaceKeepingJob(Regiment r, Vector3 pos, float rot, bool usecoverplacement, bool ignoredeploymentzone)
        {
            var pontoonJob = r.pontoonconstruction;
            var pontoon = pontoonJob != null ? pontoonJob.GetComponent<Pontoons>() : null;
            int slot = pontoon != null ? System.Array.IndexOf(pontoon.allocatedreg, r.gameObject) : -1;
            var obstacleJob = r.obstacleconstruction;
            r.SetNewPosition(pos, rot, usecoverplacement, ignoredeploymentzone);
            new BattleOrderSnap().KeepJob(r, pontoon, slot, pontoonJob, obstacleJob);
        }

        /// <summary>Is this route (or, with no route, the unit's spot) taking the unit to <paramref name="site"/>?</summary>
        private bool LeadsTo(Regiment r, Vector3 site)
        {
            var end = Waypoints.Count > 0 ? Waypoints[Waypoints.Count - 1] : r.GetPosition();
            return Tools.GetXZDistance(end, site) < 12f;
        }

        private void KeepJob(Regiment r, Pontoons pontoon, int slot, GameObject pontoonJob, GameObject obstacleJob)
        {
            if (pontoon != null && slot >= 0 && r.pontoonconstruction == null && !pontoon.completed
                && pontoon.landingzone != null && slot < pontoon.landingzone.Length && LeadsTo(r, pontoon.landingzone[slot])
                && (pontoon.allocatedreg[slot] == null || pontoon.allocatedreg[slot] == r.gameObject))
            {
                pontoon.allocatedreg[slot] = r.gameObject;
                r.pontoonconstruction = pontoonJob;
            }
            if (obstacleJob != null && r.obstacleconstruction == null && LeadsTo(r, obstacleJob.transform.position))
            {
                var line = GameApi.BUI != null ? GameApi.BUI.GetObstacleLineFromModelObj(obstacleJob) : null;
                var model = line?.modelobjects.Find(m => m.modelobj == obstacleJob);
                if (model != null && model.completion < 1f && (model.allocatedreg == null || model.allocatedreg == r.gameObject))
                {
                    model.allocatedreg = r.gameObject;
                    r.obstacleconstruction = obstacleJob;
                }
            }
        }
    }

    /// <summary>
    /// Battle orders, like the campaign's Orders but for every unit on the field:
    ///   Host: watches all units and broadcasts any change (ack = the guest's last request it applied for that unit).
    ///   Guest: reports changes to its own units that come from its own orders (UI or an arriving courier),
    ///          and applies the host's broadcasts unless it is still waiting for the host to see a newer order.
    /// </summary>
    public static class BattleOrders
    {
        private static readonly Dictionary<int, BattleOrderSnap> Known = new Dictionary<int, BattleOrderSnap>();
        /// <summary>The route each unit should be following according to the host (or the guest's own latest order).</summary>
        public static readonly Dictionary<int, BattleOrderSnap> Authoritative = new Dictionary<int, BattleOrderSnap>();
        private static readonly Dictionary<int, int> Seq = new Dictionary<int, int>();
        private static readonly HashSet<int> Dirty = new HashSet<int>();
        private static int _nextSeq;
        private static float _nextPoll;
        public static int Sent, Received, Applied, Ignored;

        public static void Reset()
        {
            Known.Clear();
            Authoritative.Clear();
            Seq.Clear();
            Dirty.Clear();
            _nextSeq = 0;
            Sent = Received = Applied = Ignored = 0;
        }

        public static void Baseline()
        {
            Known.Clear();
            foreach (var r in BattleSync.AllUnits())
            {
                var s = BattleOrderSnap.Capture(r);
                if (s.UnitId < 0) continue;
                Known[s.UnitId] = s;
                Authoritative[s.UnitId] = s;
            }
        }

        public static void MarkDirty(Regiment r) { int id = BattleSync.NetId(r); if (id >= 0) Dirty.Add(id); }

        public static void Tick()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.2f;
            if (Mp.Role == Role.Host) HostPoll();
            else GuestPoll();
        }

        // ---- host ---------------------------------------------------------------------------------------

        private static void HostPoll()
        {
            foreach (var r in BattleSync.AllUnits())
            {
                if (!r.gameObject.activeInHierarchy) continue;
                var now = BattleOrderSnap.Capture(r);
                if (Known.TryGetValue(r.unitid, out var old))
                {
                    if (now.Same(old)) continue;
                    // reaching a waypoint needs no message; a route that ends (arrived or stopped short) always does,
                    // because "stopped short" looks the same as "arrived" and the guest's copy would keep walking
                    if (now.IsProgressOf(old) && now.Waypoints.Count > 0) { Known[r.unitid] = now; continue; }
                }
                Known[r.unitid] = now;
                Broadcast(now);
            }
        }

        private static void Broadcast(BattleOrderSnap s)
        {
            Seq.TryGetValue(s.UnitId, out int ack);
            Session.Send(MsgType.BattleOrderState, Wire.Build(w => { w.Write(ack); s.Write(w); }));
            Sent++;
        }

        /// <summary>Host: the guest re-ordered one of its units.</summary>
        public static void OnGuestOrder(BinaryReader rd)
        {
            int seq = rd.ReadInt32();
            var s = BattleOrderSnap.Read(rd);
            Received++;
            var r = BattleSync.Find(s.UnitId);
            if (r == null || r.alliance != Mp.GuestAlliance || !BattleRules.GuestCommands) { Ignored++; return; }
            Seq[s.UnitId] = seq;
            // same rule as the game's order UI: routed and retreating units don't take orders
            if (r.isrouted || r.onretreat || r.surrenderstate > 0) Ignored++;
            else
            {
                Mp.Applying = true;
                try { s.ApplyTo(r); }
                finally { Mp.Applying = false; }
                Applied++;
            }
            var now = BattleOrderSnap.Capture(r);
            Known[r.unitid] = now;
            Broadcast(now);
        }

        // ---- guest --------------------------------------------------------------------------------------

        private static void GuestPoll()
        {
            if (Dirty.Count == 0) return;
            var ids = new List<int>(Dirty);
            Dirty.Clear();
            foreach (int id in ids)
            {
                var r = BattleSync.Find(id);
                if (r == null || r.alliance != Mp.GuestAlliance || !BattleRules.GuestCommands) continue;
                var now = BattleOrderSnap.Capture(r);
                if (Known.TryGetValue(id, out var old) && (now.Same(old) || now.IsProgressOf(old)))
                {
                    Known[id] = now;
                    continue;
                }
                Known[id] = now;
                Authoritative[id] = now;
                int seq = ++_nextSeq;
                Seq[id] = seq;
                Session.Send(MsgType.BattleOrder, Wire.Build(w => { w.Write(seq); now.Write(w); }));
                Sent++;
            }
        }

        /// <summary>Guest: the host's authoritative orders for one unit.</summary>
        public static void OnHostState(BinaryReader rd)
        {
            int ack = rd.ReadInt32();
            var s = BattleOrderSnap.Read(rd);
            Received++;
            // our own newer order hasn't reached the host yet: keep predicting it
            if (Seq.TryGetValue(s.UnitId, out int mine) && ack < mine) { Ignored++; return; }
            var r = BattleSync.Find(s.UnitId);
            if (r == null) { Ignored++; return; }
            Authoritative[s.UnitId] = s;
            Mp.Applying = true;
            try { s.ApplyTo(r); }
            finally { Mp.Applying = false; }
            Known[s.UnitId] = BattleOrderSnap.Capture(r);
            Applied++;
        }

        public static void Remember(Regiment r)
        {
            var s = BattleOrderSnap.Capture(r);
            if (s.UnitId >= 0) Known[s.UnitId] = s;
        }
    }
}
