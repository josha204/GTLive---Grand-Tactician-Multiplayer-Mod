using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// Individual ships (Vessel components under fleets, or in an alliance's harbour pool).
    /// Host diffs every two seconds: new ships, ships that changed fleet/pool or condition/construction/repair, and
    /// ships that are gone. Guest rebuilds ships with BattleUnits.CreateShip (initialization mode, so nothing is paid
    /// twice) and routes its own shipbuilding and ship transfers to the host.
    /// </summary>
    public static class VesselSync
    {
        private struct VState
        {
            public int Fleet, Pool, Cond, Build, Repair, Supply, Battles;
            public bool Returning;
            public bool Same(VState o) => Fleet == o.Fleet && Pool == o.Pool && Cond == o.Cond && Build == o.Build && Repair == o.Repair
                                          && Supply == o.Supply && Battles == o.Battles && Returning == o.Returning;
        }

        private static readonly Dictionary<string, VState> Known = new Dictionary<string, VState>();
        private static float _next;
        public static int Changes;

        public static string Key(Vessel v) => v.gameObject.name + "#" + v.type;

        private static VState State(Vessel v) => new VState
        {
            Fleet = v.attachedfleetregref != null ? v.attachedfleetregref.unitid : 0,
            Pool = v.attachedpool,
            Cond = Mathf.RoundToInt(v.condition * 50f),
            Build = Mathf.RoundToInt(v.constructiontimer * 50f),
            Repair = Mathf.RoundToInt(v.repairtimer * 50f),
            Supply = Mathf.RoundToInt((v.provisions + v.coal + v.ammo) * 20f),
            Battles = v.numberofbattles,
            Returning = v.returningtofleet,
        };

        public static void Reset()
        {
            Known.Clear();
            Changes = 0;
            foreach (var v in BattleUnits.GetCompleteVesselList()) Known[Key(v)] = State(v);
        }

        public static void HostTick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            var all = BattleUnits.GetCompleteVesselList();
            var created = new List<Vessel>();
            var changed = new List<Vessel>();
            var present = new HashSet<string>();
            foreach (var v in all)
            {
                string k = Key(v);
                if (!present.Add(k)) continue;
                var s = State(v);
                if (!Known.TryGetValue(k, out var old)) created.Add(v);
                else if (!old.Same(s)) changed.Add(v);
                Known[k] = s;
            }
            var removed = Known.Keys.Where(k => !present.Contains(k)).ToList();
            foreach (var k in removed) Known.Remove(k);
            if (created.Count + changed.Count + removed.Count == 0) return;
            Changes += created.Count + changed.Count + removed.Count;
            Session.Send(MsgType.Vessels, Wire.Build(w =>
            {
                w.Write(created.Count);
                foreach (var v in created)
                {
                    w.WriteStr(v.gameObject.name);
                    w.Write(v.type);
                    w.Write(v.currentposition);
                    w.Write(v.destination);
                    w.Write(v.timeofarrival);
                    w.Write(v.harbortoreturn != null ? v.harbortoreturn.transform.position : default(Vector3));
                    WriteState(w, v);
                }
                w.Write(changed.Count);
                foreach (var v in changed)
                {
                    w.WriteStr(Key(v));
                    WriteState(w, v);
                }
                w.Write(removed.Count);
                foreach (var k in removed) w.WriteStr(k);
            }));
        }

        private static void WriteState(BinaryWriter w, Vessel v)
        {
            w.Write(v.attachedfleetregref != null ? v.attachedfleetregref.unitid : 0);
            w.Write(v.attachedpool);
            w.Write(v.condition); w.Write(v.provisions); w.Write(v.coal); w.Write(v.ammo);
            w.Write(v.numberofbattles);
            w.Write(v.constructiontimer); w.Write(v.repairtimer);
            w.Write(v.returningtofleet);
        }

        public static void OnVessels(BinaryReader r)
        {
            var bu = GameApi.BU;
            var byKey = new Dictionary<string, Vessel>();
            foreach (var v in BattleUnits.GetCompleteVesselList()) byKey[Key(v)] = v;
            Mp.Applying = true;
            try
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string name = r.ReadString();
                    int type = r.ReadInt32();
                    var pos = r.ReadVector3();
                    var dest = r.ReadVector3();
                    float arrival = r.ReadSingle();
                    var home = r.ReadVector3();
                    int fleet = r.ReadInt32(), pool = r.ReadInt32();
                    float cond = r.ReadSingle(), prov = r.ReadSingle(), coal = r.ReadSingle(), ammo = r.ReadSingle();
                    int battles = r.ReadInt32();
                    float build = r.ReadSingle(), repair = r.ReadSingle();
                    bool returning = r.ReadBoolean();
                    if (byKey.ContainsKey(name + "#" + type)) continue;
                    var fleetReg = fleet != 0 ? GameApi.FindUnit(fleet) : null;
                    var v = bu.CreateShip(name, fleetReg, fleetReg != null ? -1 : pool, type, cond, prov, coal, ammo, battles, pos, dest, arrival,
                        initialization: true, underconstruction: build > 0f, repair, home, returning, build);
                    if (v != null) byKey[Key(v)] = v;
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string key = r.ReadString();
                    int fleet = r.ReadInt32(), pool = r.ReadInt32();
                    float cond = r.ReadSingle(), prov = r.ReadSingle(), coal = r.ReadSingle(), ammo = r.ReadSingle();
                    int battles = r.ReadInt32();
                    float build = r.ReadSingle(), repair = r.ReadSingle();
                    bool returning = r.ReadBoolean();
                    if (!byKey.TryGetValue(key, out var v)) continue;
                    int curFleet = v.attachedfleetregref != null ? v.attachedfleetregref.unitid : 0;
                    if (fleet != curFleet || (fleet == 0 && pool != v.attachedpool))
                    {
                        var fleetReg = fleet != 0 ? GameApi.FindUnit(fleet) : null;
                        if (fleetReg != null) BattleUnits.MoveShip(v, fleetReg);
                        else if (pool >= 0) BattleUnits.MoveShip(v, pool);
                    }
                    v.condition = cond; v.provisions = prov; v.coal = coal; v.ammo = ammo;
                    v.numberofbattles = battles;
                    v.constructiontimer = build;
                    v.repairtimer = repair;
                    v.returningtofleet = returning;
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string key = r.ReadString();
                    if (byKey.TryGetValue(key, out var v) && v != null) BattleUnits.DeleteShip(v.gameObject);
                }
            }
            finally { Mp.Applying = false; }
            Changes++;
        }

        // ---- host side of the guest's ship requests ---------------------------------------------------

        public static void OnRequest(BinaryReader r)
        {
            bool build = r.ReadBoolean();
            if (build)
            {
                string name = r.ReadString();
                int fleet = r.ReadInt32(), pool = r.ReadInt32(), type = r.ReadInt32();
                var fleetReg = fleet != 0 ? GameApi.FindUnit(fleet) : null;
                if (fleetReg != null && fleetReg.alliance != Mp.GuestAlliance) return;
                if (fleetReg == null && pool != Mp.GuestAlliance) return;
                int saved = GameVars.playeralliance;
                GameVars.playeralliance = Mp.GuestAlliance; // import costs are priced for the player's alliance
                try
                {
                    GameApi.BU.CreateShip(name, fleetReg, fleetReg != null ? -1 : pool, type, 1f, 1f, 1f, 1f, 0, default, default, 0f,
                        initialization: false, underconstruction: true, 0f, default, returntofleet: false, 1f);
                }
                finally { GameVars.playeralliance = saved; }
            }
            else
            {
                string key = r.ReadString();
                int fleet = r.ReadInt32(), pool = r.ReadInt32();
                var v = BattleUnits.GetCompleteVesselList().FirstOrDefault(x => Key(x) == key);
                if (v == null) return;
                int owner = v.attachedfleetregref != null ? v.attachedfleetregref.alliance : v.attachedpool;
                if (owner != Mp.GuestAlliance) return;
                var fleetReg = fleet != 0 ? GameApi.FindUnit(fleet) : null;
                if (fleetReg != null && fleetReg.alliance == Mp.GuestAlliance) BattleUnits.MoveShip(v, fleetReg);
                else if (fleetReg == null && pool == Mp.GuestAlliance) BattleUnits.MoveShip(v, pool);
            }
        }
    }

    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.CreateShip), typeof(string), typeof(Regiment), typeof(int), typeof(int), typeof(float), typeof(float), typeof(float), typeof(float), typeof(int), typeof(Vector3), typeof(Vector3), typeof(float), typeof(bool), typeof(bool), typeof(float), typeof(Vector3), typeof(bool), typeof(float))]
    internal static class GuestBuildShip
    {
        private static bool Prefix(string shipname, Regiment attachedtofleet, int attachedtopool, int shiptype, bool initialization, bool underconstruction, ref Vessel __result)
        {
            if (!GuestRoute.Active || initialization || !underconstruction) return true;
            int owner = attachedtofleet != null ? attachedtofleet.alliance : attachedtopool;
            if (owner != Mp.GuestAlliance) return true;
            int fleet = attachedtofleet != null ? attachedtofleet.unitid : 0;
            Session.Send(MsgType.VesselRequest, Wire.Build(w =>
            {
                w.Write(true);
                w.WriteStr(shipname);
                w.Write(fleet);
                w.Write(attachedtopool);
                w.Write(shiptype);
            }));
            Session.Notice($"Ship construction ({shipname}) sent to the host.");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.MoveShip), typeof(Vessel), typeof(Regiment))]
    internal static class GuestMoveShipToFleet
    {
        private static void Prefix(Vessel vesseltomove, Regiment fleettomoveto)
        {
            if (!GuestRoute.Active || vesseltomove == null || fleettomoveto == null || fleettomoveto.alliance != Mp.GuestAlliance) return;
            string key = VesselSync.Key(vesseltomove);
            Session.Send(MsgType.VesselRequest, Wire.Build(w => { w.Write(false); w.WriteStr(key); w.Write(fleettomoveto.unitid); w.Write(-1); }));
        }
    }

    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.MoveShip), typeof(Vessel), typeof(int))]
    internal static class GuestMoveShipToPool
    {
        private static void Prefix(Vessel vesseltomove, int alliancepool)
        {
            if (!GuestRoute.Active || vesseltomove == null || alliancepool != Mp.GuestAlliance) return;
            string key = VesselSync.Key(vesseltomove);
            Session.Send(MsgType.VesselRequest, Wire.Build(w => { w.Write(false); w.WriteStr(key); w.Write(0); w.Write(alliancepool); }));
        }
    }
}
