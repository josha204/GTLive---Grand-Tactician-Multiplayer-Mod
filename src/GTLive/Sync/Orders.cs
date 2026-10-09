using System.Collections.Generic;
using System.IO;
using GTLive.Net;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// What a campaign formation has been told to do: its remaining waypoints plus the order flags the save
    /// file keeps in paths.dat. Replaying a snapshot uses the same calls the game uses when it restores a save
    /// (Regiment.WorkDownStoredPaths), so any UI route that changes an order is covered by watching the result.
    /// </summary>
    public class OrderSnap
    {
        public int UnitId;
        public List<Vector3> Waypoints = new List<Vector3>();
        public List<int> PathStatus = new List<int>();
        public float FinalRotation;
        public bool UseSea, UseSeaOnly, UseRivers, UseRails, UseRoads;
        public int Stance, StanceOrdered, Cavalry, CavalryOrdered, Fleet;

        public static OrderSnap Capture(Regiment r)
        {
            var s = new OrderSnap { UnitId = r.unitid };
            int start = 0;
            if (r.blockobjectcurrentpath != null && r.blockobjectcurrentpath.Length > 0
                && r.blockobjectcurrentpath[0] >= 0 && r.blockobjectcurrentpath[0] < r.regimentpaths)
                start = r.blockobjectcurrentpath[0];
            if (r.regimentpath != null)
            {
                for (int k = start; k < r.regimentpaths && k < r.regimentpath.Length; k++)
                {
                    var p = r.regimentpath[k];
                    if (p == null || p.corners == null || p.corners.Length == 0) continue;
                    s.Waypoints.Add(p.corners[p.corners.Length - 1]);
                    s.PathStatus.Add(r.pathstatus != null && k < r.pathstatus.Length ? r.pathstatus[k] : 2);
                }
            }
            s.FinalRotation = r.lastwaypointrotation != null && r.lastwaypointrotation.Length > 0
                ? r.lastwaypointrotation[0] : r.transform.rotation.eulerAngles.y;
            s.UseSea = r.usesea; s.UseSeaOnly = r.useseaonly; s.UseRivers = r.userivers; s.UseRails = r.userails; s.UseRoads = r.useroads;
            s.Stance = r.campaignstance; s.StanceOrdered = r.campaignstanceordered;
            s.Cavalry = r.cavalryorders; s.CavalryOrdered = r.cavalryordersordered; s.Fleet = r.fleetorders;
            return s;
        }

        public bool SameFlags(OrderSnap o) =>
            UseSea == o.UseSea && UseSeaOnly == o.UseSeaOnly && UseRivers == o.UseRivers && UseRails == o.UseRails && UseRoads == o.UseRoads
            && StanceOrdered == o.StanceOrdered && CavalryOrdered == o.CavalryOrdered && Fleet == o.Fleet;

        public static bool Near(Vector3 a, Vector3 b) => (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z) < 0.25f;

        public bool SameRoute(OrderSnap o)
        {
            if (Waypoints.Count != o.Waypoints.Count) return false;
            for (int i = 0; i < Waypoints.Count; i++)
                if (!Near(Waypoints[i], o.Waypoints[i])) return false;
            return true;
        }

        /// <summary>True if this route is <paramref name="older"/> with one or more leading waypoints already reached.</summary>
        public bool IsProgressOf(OrderSnap older)
        {
            int drop = older.Waypoints.Count - Waypoints.Count;
            if (drop <= 0) return false;
            for (int i = 0; i < Waypoints.Count; i++)
                if (!Near(Waypoints[i], older.Waypoints[i + drop])) return false;
            return true;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(UnitId);
            w.Write((byte)Waypoints.Count);
            for (int i = 0; i < Waypoints.Count; i++)
            {
                w.Write(Waypoints[i]);
                w.Write((byte)PathStatus[i]);
            }
            w.Write(FinalRotation);
            byte flags = (byte)((UseSea ? 1 : 0) | (UseSeaOnly ? 2 : 0) | (UseRivers ? 4 : 0) | (UseRails ? 8 : 0) | (UseRoads ? 16 : 0));
            w.Write(flags);
            w.Write((sbyte)Stance); w.Write((sbyte)StanceOrdered);
            w.Write((sbyte)Cavalry); w.Write((sbyte)CavalryOrdered); w.Write((sbyte)Fleet);
        }

        public static OrderSnap Read(BinaryReader r)
        {
            var s = new OrderSnap { UnitId = r.ReadInt32() };
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                s.Waypoints.Add(r.ReadVector3());
                s.PathStatus.Add(r.ReadByte());
            }
            s.FinalRotation = r.ReadSingle();
            byte f = r.ReadByte();
            s.UseSea = (f & 1) != 0; s.UseSeaOnly = (f & 2) != 0; s.UseRivers = (f & 4) != 0; s.UseRails = (f & 8) != 0; s.UseRoads = (f & 16) != 0;
            s.Stance = r.ReadSByte(); s.StanceOrdered = r.ReadSByte();
            s.Cavalry = r.ReadSByte(); s.CavalryOrdered = r.ReadSByte(); s.Fleet = r.ReadSByte();
            return s;
        }

        /// <summary>Makes <paramref name="r"/> follow this snapshot, the way the game restores paths from a save.</summary>
        public void ApplyTo(Regiment r)
        {
            var cur = Capture(r);
            r.usesea = UseSea; r.useseaonly = UseSeaOnly; r.userivers = UseRivers; r.userails = UseRails; r.useroads = UseRoads;
            r.campaignstanceordered = StanceOrdered;
            r.campaignstance = Stance;
            r.cavalryordersordered = CavalryOrdered;
            r.cavalryorders = Cavalry;
            r.fleetorders = Fleet;
            if (SameRoute(cur)) return;
            if (Waypoints.Count == 0)
            {
                r.StopRegiment(skipfinalrotation: true, manualstop: true, skipmarchcolumnstop: false, skipadvancefallbackstop: false, clearregimentpathsbeforerotation: false, playstopbugle: false);
                return;
            }
            r.immediateunitplacement = false;
            for (int j = 0; j < Waypoints.Count; j++)
            {
                float rot = j == Waypoints.Count - 1 ? FinalRotation : -1f;
                r.RegimentSetPath(Waypoints[j], newpath: j == 0, skipcover: false, rot, modifylastwaypoint: false, PathStatus[j], -1f, null, autoadvancefallback: true, uimovement: false);
            }
        }
    }

    /// <summary>
    /// Host: watches every campaign formation and broadcasts route changes; applies the guest's requests.
    /// Guest: watches its own formations and sends route changes as requests; applies the host's broadcasts.
    /// </summary>
    public static class Orders
    {
        private static readonly Dictionary<int, OrderSnap> Known = new Dictionary<int, OrderSnap>();
        // guest: newest request sequence per unit; host: last request sequence it applied per unit
        private static readonly Dictionary<int, int> Seq = new Dictionary<int, int>();
        private static int _nextSeq;
        private static float _nextPoll;
        public static int Sent, Received, Applied, Ignored;

        public static void Reset()
        {
            Known.Clear();
            Seq.Clear();
            _nextSeq = 0;
            Sent = Received = Applied = Ignored = 0;
        }

        /// <summary>Records the current state of every unit without sending anything (after a load or resync).</summary>
        public static void Baseline()
        {
            Known.Clear();
            foreach (var r in CampaignUnits()) Known[r.unitid] = OrderSnap.Capture(r);
        }

        public static IEnumerable<Regiment> CampaignUnits()
        {
            foreach (var r in GameApi.Units())
                if (r.gameObject.activeInHierarchy && BattleUnits.GetUnitVisibilityStateOnCampaignMap(r) > 0)
                    yield return r;
        }

        public static void Tick()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.25f;
            bool host = Mp.Role == Role.Host;
            foreach (var r in CampaignUnits())
            {
                if (!host && r.alliance != Mp.GuestAlliance) continue;
                var now = OrderSnap.Capture(r);
                if (Known.TryGetValue(r.unitid, out var old))
                {
                    if (now.SameRoute(old) && now.SameFlags(old)) continue;
                    if (now.IsProgressOf(old) && now.SameFlags(old)) { Known[r.unitid] = now; continue; }
                }
                Known[r.unitid] = now;
                if (old == null) continue; // first sighting (new unit): nothing to report yet
                if (host) Broadcast(now);
                else Request(now);
            }
        }

        private static void Broadcast(OrderSnap s)
        {
            Seq.TryGetValue(s.UnitId, out int ack);
            Session.Send(MsgType.OrderState, Wire.Build(w => { w.Write(ack); s.Write(w); }));
            Sent++;
        }

        private static void Request(OrderSnap s)
        {
            int seq = ++_nextSeq;
            Seq[s.UnitId] = seq;
            Session.Send(MsgType.OrderRequest, Wire.Build(w => { w.Write(seq); s.Write(w); }));
            Sent++;
        }

        /// <summary>Host: the guest changed one of its formations' orders.</summary>
        public static void OnRequest(BinaryReader rd)
        {
            int seq = rd.ReadInt32();
            var s = OrderSnap.Read(rd);
            Received++;
            var r = GameApi.FindUnit(s.UnitId);
            if (r == null || r.alliance != Mp.GuestAlliance) { Ignored++; return; }
            Seq[s.UnitId] = seq;
            // same rules as BattleUnits.SetWaypoint: retreating, routed or engaged formations can't be re-ordered
            if (r.onretreat || r.isrouted || r.inbattle)
            {
                Ignored++;
            }
            else
            {
                Mp.Applying = true;
                try { s.ApplyTo(r); }
                finally { Mp.Applying = false; }
                Applied++;
            }
            // always answer, even if the order was refused, so the guest stops predicting
            var now = OrderSnap.Capture(r);
            Known[r.unitid] = now;
            Broadcast(now);
        }

        /// <summary>Guest: authoritative order state from the host.</summary>
        public static void OnState(BinaryReader rd)
        {
            int ack = rd.ReadInt32();
            var s = OrderSnap.Read(rd);
            Received++;
            if (Seq.TryGetValue(s.UnitId, out int mine) && ack < mine) { Ignored++; return; }
            var r = GameApi.FindUnit(s.UnitId);
            if (r == null) { Ignored++; return; }
            Mp.Applying = true;
            try { s.ApplyTo(r); }
            finally { Mp.Applying = false; }
            Known[r.unitid] = OrderSnap.Capture(r);
            Applied++;
        }
    }
}
