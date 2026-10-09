using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// The map itself: who owns each town and industrial site, and the campaign buildings (depots, forts, companies,
    /// telegraph stations, POW camps) - which exist, their owner, level, condition and construction progress.
    /// Host diffs every two seconds and sends changes; the guest applies them directly (the host already ran the
    /// side effects such as garrisons and national morale, which reach the guest through the other channels).
    /// </summary>
    public static class WorldSync
    {
        private struct BState
        {
            public int Type, Owner, Level, Cond, Timer;
            public bool Same(BState o) => Type == o.Type && Owner == o.Owner && Level == o.Level && Cond == o.Cond && Timer == o.Timer;
        }

        private static int[] _townOwner = new int[0];
        private static int[] _iipOwner = new int[0];
        private static int[] _railState = new int[0];

        private static int RailState(BattleUnits.Railroad rr) => (rr.alliancetoconstruct + 1) * 1000 + Mathf.RoundToInt(Mathf.Clamp01(rr.constructionprogress) * 200f);
        private static readonly Dictionary<string, BState> Buildings = new Dictionary<string, BState>();
        private static float _next;
        public static int Changes;

        // guest: buildings the guest placed itself and the host hasn't confirmed yet
        private static readonly Dictionary<CBuilding, float> Provisional = new Dictionary<CBuilding, float>();

        public static string Key(CBuilding b)
        {
            var p = b.transform.position;
            return b.gameObject.name + "@" + Mathf.RoundToInt(p.x * 2f) + "," + Mathf.RoundToInt(p.z * 2f);
        }

        private static BState State(CBuilding b) => new BState
        {
            Type = b.BuildingType, Owner = b.Owner, Level = b.level,
            Cond = Mathf.RoundToInt(b.Condition * 20f), Timer = Mathf.RoundToInt(b.constructiontimer * 20f),
        };

        public static void Reset()
        {
            Changes = 0;
            Provisional.Clear();
            _townOwner = BattleUnits.towns?.Select(t => t.Owner).ToArray() ?? new int[0];
            _iipOwner = BattleUnits.iips?.Select(i => i.allianceowner).ToArray() ?? new int[0];
            _railState = BattleUnits.railroad?.Select(RailState).ToArray() ?? new int[0];
            Buildings.Clear();
            if (BattleUnits.cbuildings != null)
                foreach (var b in BattleUnits.cbuildings)
                    if (b != null) Buildings[Key(b)] = State(b);
        }

        // ---- host -------------------------------------------------------------------------------------

        public static void HostTick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;

            var towns = new List<KeyValuePair<int, int>>();
            var tl = BattleUnits.towns;
            if (tl != null)
            {
                if (_townOwner.Length != tl.Count) _townOwner = tl.Select(t => int.MinValue).ToArray();
                for (int i = 0; i < tl.Count; i++)
                    if (tl[i] != null && tl[i].Owner != _townOwner[i]) { _townOwner[i] = tl[i].Owner; towns.Add(new KeyValuePair<int, int>(i, tl[i].Owner)); }
            }
            var iips = new List<KeyValuePair<int, int>>();
            var il = BattleUnits.iips;
            if (il != null)
            {
                if (_iipOwner.Length != il.Count) _iipOwner = il.Select(t => int.MinValue).ToArray();
                for (int i = 0; i < il.Count; i++)
                    if (il[i] != null && il[i].allianceowner != _iipOwner[i]) { _iipOwner[i] = il[i].allianceowner; iips.Add(new KeyValuePair<int, int>(i, il[i].allianceowner)); }
            }

            var rails = new List<int>();
            var rl = BattleUnits.railroad;
            if (rl != null)
            {
                if (_railState.Length != rl.Count) _railState = rl.Select(x => int.MinValue).ToArray();
                for (int i = 0; i < rl.Count; i++)
                {
                    int s = RailState(rl[i]);
                    if (s != _railState[i]) { _railState[i] = s; rails.Add(i); }
                }
            }

            var created = new List<CBuilding>();
            var changed = new List<CBuilding>();
            var present = new HashSet<string>();
            if (BattleUnits.cbuildings != null)
            {
                foreach (var b in BattleUnits.cbuildings)
                {
                    if (b == null || b == CBuilding.manualplacedbuilding) continue;
                    string k = Key(b);
                    present.Add(k);
                    var s = State(b);
                    if (!Buildings.TryGetValue(k, out var old)) created.Add(b);
                    else if (!old.Same(s)) changed.Add(b);
                    Buildings[k] = s;
                }
            }
            var removed = Buildings.Keys.Where(k => !present.Contains(k)).ToList();
            foreach (var k in removed) Buildings.Remove(k);

            int total = towns.Count + iips.Count + rails.Count + created.Count + changed.Count + removed.Count;
            if (total == 0) return;
            Changes += total;
            Session.Send(MsgType.World, Wire.Build(w =>
            {
                w.Write(towns.Count);
                foreach (var kv in towns) { w.Write(kv.Key); w.Write(kv.Value); }
                w.Write(iips.Count);
                foreach (var kv in iips) { w.Write(kv.Key); w.Write(kv.Value); }
                w.Write(rails.Count);
                foreach (int i in rails) { w.Write(i); w.Write(rl[i].alliancetoconstruct); w.Write(rl[i].constructionprogress); }
                w.Write(created.Count);
                foreach (var b in created)
                {
                    w.WriteStr(b.gameObject.name);
                    w.WriteStr(b.BuildingName);
                    w.Write(b.transform.position);
                    w.Write(b.transform.rotation.eulerAngles.y);
                    WriteState(w, b);
                }
                w.Write(changed.Count);
                foreach (var b in changed)
                {
                    w.WriteStr(Key(b));
                    WriteState(w, b);
                }
                w.Write(removed.Count);
                foreach (var k in removed) w.WriteStr(k);
            }));
        }

        private static void WriteState(BinaryWriter w, CBuilding b)
        {
            w.Write(b.BuildingType);
            w.Write(b.Owner);
            w.Write(b.level);
            w.Write(b.Condition);
            w.Write(b.constructiontimer);
            w.Write(b.allianceconstructionstarted);
        }

        // ---- guest ------------------------------------------------------------------------------------

        public static void OnWorld(BinaryReader r)
        {
            var bu = GameApi.BU;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int idx = r.ReadInt32(), owner = r.ReadInt32();
                var t = BattleUnits.towns != null && idx < BattleUnits.towns.Count ? BattleUnits.towns[idx] : null;
                if (t == null || t.Owner == owner) continue;
                t.Owner = owner;
                if (owner >= 0) bu.frontline2.ChangeFrontlineObjectSide(t.gameObject, owner);
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int idx = r.ReadInt32(), owner = r.ReadInt32();
                var p = BattleUnits.iips != null && idx < BattleUnits.iips.Count ? BattleUnits.iips[idx] : null;
                if (p != null) p.allianceowner = owner;
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int idx = r.ReadInt32(), builder = r.ReadInt32();
                float progress = r.ReadSingle();
                var rr = BattleUnits.railroad != null && idx < BattleUnits.railroad.Count ? BattleUnits.railroad[idx] : null;
                if (rr == null || rr.constructionprogress >= 1f) continue;
                rr.alliancetoconstruct = builder;
                // let the guest's own UpdateConstruction finish a completed line so it shows the tracks and the newspaper
                rr.constructionprogress = progress >= 1f ? 0.99999f : progress;
                if (rr.lastconstructionupdate <= 0f) rr.lastconstructionupdate = GameVars.GetCampaignFloatingDate();
            }

            Mp.Applying = true;
            try
            {
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string goName = r.ReadString(), bname = r.ReadString();
                    var pos = r.ReadVector3();
                    float angle = r.ReadSingle();
                    int type = r.ReadInt32(), owner = r.ReadInt32(), level = r.ReadInt32();
                    float cond = r.ReadSingle(), timer = r.ReadSingle();
                    int started = r.ReadInt32();
                    var b = AdoptProvisional(type, pos);
                    if (b == null)
                    {
                        b = CBuilding.Place(pos, type, owner, level, angle, cond, newlycreated: false, timer, pay: false);
                        if (b == null) continue;
                    }
                    b.gameObject.name = goName;
                    b.BuildingName = bname;
                    ApplyState(b, owner, level, cond, timer, started);
                    if (!BattleUnits.cbuildings.Contains(b)) BattleUnits.cbuildings.Add(b);
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string key = r.ReadString();
                    int type = r.ReadInt32(), owner = r.ReadInt32(), level = r.ReadInt32();
                    float cond = r.ReadSingle(), timer = r.ReadSingle();
                    int started = r.ReadInt32();
                    var b = BattleUnits.cbuildings.FirstOrDefault(x => x != null && Key(x) == key);
                    if (b != null) ApplyState(b, owner, level, cond, timer, started);
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string key = r.ReadString();
                    var b = BattleUnits.cbuildings.FirstOrDefault(x => x != null && Key(x) == key);
                    b?.RemoveBuilding();
                }
            }
            finally { Mp.Applying = false; }
            Changes++;
        }

        private static void ApplyState(CBuilding b, int owner, int level, float cond, float timer, int started)
        {
            if (b.Owner != owner)
            {
                b.Owner = owner;
                Traverse.Create(b).Field("oldowner").SetValue(owner);
            }
            if (b.level != level) b.SetLevel(level);
            b.Condition = cond;
            b.constructiontimer = timer;
            b.allianceconstructionstarted = started;
        }

        public static void NoteProvisional(CBuilding b)
        {
            if (b != null) Provisional[b] = Time.unscaledTime;
        }

        private static CBuilding AdoptProvisional(int type, Vector3 pos)
        {
            foreach (var kv in Provisional)
            {
                var b = kv.Key;
                if (b == null || b.BuildingType != type) continue;
                if ((b.transform.position - pos).sqrMagnitude > 1f) continue;
                Provisional.Remove(b);
                b.transform.position = pos;
                return b;
            }
            return null;
        }

        /// <summary>Guest: buildings the host never confirmed (refused or failed) disappear after a while.</summary>
        public static void GuestTick()
        {
            if (Provisional.Count == 0) return;
            foreach (var b in Provisional.Where(kv => kv.Key == null || Time.unscaledTime - kv.Value > 15f).Select(kv => kv.Key).ToList())
            {
                Provisional.Remove(b);
                if (b != null)
                {
                    Session.Notice($"The host did not confirm {b.BuildingName}; removing it.");
                    b.RemoveBuilding();
                }
            }
        }
    }

    [HarmonyPatch(typeof(CBuilding), nameof(CBuilding.Place))]
    internal static class TrackProvisionalBuilding
    {
        private static void Postfix(CBuilding __result, int owner, bool newlycreated)
        {
            if (GuestRoute.Active && newlycreated && owner == Mp.GuestAlliance) WorldSync.NoteProvisional(__result);
        }
    }
}
