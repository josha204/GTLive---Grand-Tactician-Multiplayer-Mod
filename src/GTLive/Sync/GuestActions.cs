using System;
using System.IO;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Sync
{
    public enum ActionKind : byte
    {
        AddResearch = 1, RemoveResearch = 2, AppointProject = 3, WeaponOrder = 4, AssignWeapon = 5,
        AssignCommander = 6, PlaceBuilding = 7, StartRailroad = 8,
    }

    /// <summary>
    /// The guest's economic and command actions on its own alliance. They run locally right away (so the UI responds)
    /// and are replayed on the host, where they become real; the results come back through economy, structure,
    /// commander and building sync. On the host they run "as the guest": GameVars.playeralliance is switched to the
    /// guest's alliance for the duration, because some of these routines look it up directly.
    /// </summary>
    public static class GuestActions
    {
        public static int Sent, Applied;

        public static void Send(ActionKind kind, Action<BinaryWriter> body)
        {
            Session.Send(MsgType.GuestAction, Wire.Build(w => { w.Write((byte)kind); body(w); }));
            Sent++;
        }

        public static void OnAction(BinaryReader r)
        {
            var kind = (ActionKind)r.ReadByte();
            int g = Mp.GuestAlliance;
            int saved = GameVars.playeralliance;
            Mp.Applying = true;
            // weapon orders compute delivery time from GameVars.playeralliance; everything else takes the alliance explicitly
            // (and must not switch it, or the host's open panels would refresh with the guest's data)
            if (kind == ActionKind.WeaponOrder) GameVars.playeralliance = g;
            try
            {
                switch (kind)
                {
                    case ActionKind.AddResearch:
                    {
                        var p = Policy.GetPolicyFromID(r.ReadInt32());
                        if (p != null) Policies.AddResearch(g, p);
                        Policies.UpdateCounter(saved);
                        break;
                    }
                    case ActionKind.RemoveResearch:
                    {
                        var p = Policy.GetPolicyFromID(r.ReadInt32());
                        if (p != null) Policies.RemoveResearch(g, p);
                        Policies.UpdateCounter(saved);
                        break;
                    }
                    case ActionKind.AppointProject:
                    {
                        var p = Projects.LoadedProjects.GetLoadedProjectFromID(r.ReadInt32());
                        bool manual = r.ReadBoolean();
                        if (p != null) Projects.AppointProject(p, g, manual);
                        break;
                    }
                    case ActionKind.WeaponOrder:
                    {
                        int weapon = r.ReadInt32(), qty = r.ReadInt32();
                        WeaponList.PlaceWeaponOrder(g, weapon, qty);
                        break;
                    }
                    case ActionKind.AssignWeapon:
                    {
                        int weapon = r.ReadInt32();
                        var unit = GameApi.FindUnit(r.ReadInt32());
                        if (unit != null && unit.alliance == g) AssignWeapon(weapon, unit);
                        break;
                    }
                    case ActionKind.AssignCommander:
                    {
                        int id = r.ReadInt32();
                        var unit = GameApi.FindUnit(r.ReadInt32());
                        bool updatename = r.ReadBoolean(), removal = r.ReadBoolean();
                        if (unit != null && unit.alliance == g && id >= 0 && id < GameVars.commander.Count && GameVars.commander[id].alliance == g)
                            GameVars.commander[id].AssignCommando(unit, updatename, removal);
                        break;
                    }
                    case ActionKind.PlaceBuilding:
                    {
                        var pos = r.ReadVector3();
                        int type = r.ReadInt32(), level = r.ReadInt32();
                        float angle = r.ReadSingle(), condition = r.ReadSingle(), timer = r.ReadSingle();
                        bool pay = r.ReadBoolean();
                        CBuilding.Place(pos, type, g, level, angle, condition, newlycreated: true, timer, pay);
                        break;
                    }
                    case ActionKind.StartRailroad:
                    {
                        int idx = r.ReadInt32();
                        var rails = BattleUnits.railroad;
                        if (rails != null && idx >= 0 && idx < rails.Count) rails[idx].StartConstruction(g);
                        break;
                    }
                }
                Applied++;
            }
            finally
            {
                GameVars.playeralliance = saved;
                Mp.Applying = false;
            }
        }

        /// <summary>WeaponList.AssignWeapon without its UI refresh (it would reset the host player's panel).</summary>
        private static void AssignWeapon(int weapon, Regiment unit)
        {
            int n = unit.strength + unit.losses[0] + unit.sick;
            if (unit.unittyp == 2) n = unit.statsguns;
            var a = GameVars.alliance[unit.alliance];
            if (!GameVars.weapon[weapon].IsWeaponInStock(unit.alliance, weapon, n)) return;
            if (unit.weapon >= 0) a.weaponstock[unit.weapon] += n;
            a.weaponunitsequipped[weapon] += n;
            a.weaponstock[weapon] = Mathf.Max(0f, a.weaponstock[weapon] - n);
            if (unit.weapon >= 0) a.weaponunitsequipped[unit.weapon] -= n;
            unit.weapon = weapon;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Guest-side capture
    // ---------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(Policies), nameof(Policies.AddResearch))]
    internal static class CaptureAddResearch
    {
        private static void Prefix(int alliance, Policy policy)
        {
            if (!GuestRoute.Active || alliance != Mp.GuestAlliance || policy == null) return;
            int id = Policy.GetIDFromPolicy(policy);
            GuestActions.Send(ActionKind.AddResearch, w => w.Write(id));
        }
    }

    [HarmonyPatch(typeof(Policies), nameof(Policies.RemoveResearch), typeof(int), typeof(Policy))]
    internal static class CaptureRemoveResearch
    {
        private static void Prefix(int alliance, Policy policy)
        {
            if (!GuestRoute.Active || alliance != Mp.GuestAlliance || policy == null) return;
            int id = Policy.GetIDFromPolicy(policy);
            GuestActions.Send(ActionKind.RemoveResearch, w => w.Write(id));
        }
    }

    [HarmonyPatch(typeof(Projects), nameof(Projects.AppointProject))]
    internal static class CaptureAppointProject
    {
        private static void Prefix(Projects.LoadedProjects project, int alliance, bool manualappointment)
        {
            if (!GuestRoute.Active || alliance != Mp.GuestAlliance || project == null) return;
            int id = project.projectid;
            GuestActions.Send(ActionKind.AppointProject, w => { w.Write(id); w.Write(manualappointment); });
        }
    }

    [HarmonyPatch(typeof(WeaponList), nameof(WeaponList.PlaceWeaponOrder))]
    internal static class CaptureWeaponOrder
    {
        private static void Prefix(int alliance, int weaponid, int quantity)
        {
            if (!GuestRoute.Active || alliance != Mp.GuestAlliance) return;
            GuestActions.Send(ActionKind.WeaponOrder, w => { w.Write(weaponid); w.Write(quantity); });
        }
    }

    [HarmonyPatch(typeof(WeaponList), nameof(WeaponList.AssignWeapon))]
    internal static class CaptureAssignWeapon
    {
        private static void Prefix(WeaponList __instance, int manualweapon, Regiment manualunit)
        {
            if (!GuestRoute.Active) return;
            var t = Traverse.Create(__instance);
            int weapon = manualweapon >= 0 ? manualweapon : t.Field("selectedweapon").GetValue<int>();
            var unit = manualunit != null ? manualunit : t.Field("unittocall").GetValue<Regiment>();
            if (weapon < 0 || unit == null || unit.alliance != Mp.GuestAlliance) return;
            GuestActions.Send(ActionKind.AssignWeapon, w => { w.Write(weapon); w.Write(unit.unitid); });
        }
    }

    /// <summary>Only assignments made in the commander panels count as the guest's decision.</summary>
    internal static class CommanderUi
    {
        public static int Depth;
    }

    [HarmonyPatch(typeof(CommandersList), nameof(CommandersList.AssignCommander))]
    internal static class CommanderUiList
    {
        private static void Prefix() => CommanderUi.Depth++;
        private static void Finalizer() => CommanderUi.Depth--;
    }

    [HarmonyPatch(typeof(CommanderPanel), nameof(CommanderPanel.AssignCommanderID))]
    internal static class CommanderUiPanel
    {
        private static void Prefix() => CommanderUi.Depth++;
        private static void Finalizer() => CommanderUi.Depth--;
    }

    [HarmonyPatch(typeof(GameVars.Commander), nameof(GameVars.Commander.AssignCommando))]
    internal static class CaptureAssignCommander
    {
        private static void Prefix(GameVars.Commander __instance, Regiment reg, bool updatename, bool removalofoldcommander)
        {
            if (CommanderUi.Depth <= 0 || !GuestRoute.Active || reg == null || reg.alliance != Mp.GuestAlliance) return;
            int id = GameVars.commander.IndexOf(__instance);
            if (id < 0) return;
            GuestActions.Send(ActionKind.AssignCommander, w => { w.Write(id); w.Write(reg.unitid); w.Write(updatename); w.Write(removalofoldcommander); });
        }
    }

    [HarmonyPatch(typeof(BattleUnits.Railroad), nameof(BattleUnits.Railroad.StartConstruction))]
    internal static class CaptureStartRailroad
    {
        private static void Postfix(BattleUnits.Railroad __instance, int _alliancetoconstruct, bool checkonly, bool __result)
        {
            if (!GuestRoute.Active || checkonly || !__result || _alliancetoconstruct != Mp.GuestAlliance) return;
            int idx = BattleUnits.railroad.IndexOf(__instance);
            if (idx >= 0) GuestActions.Send(ActionKind.StartRailroad, w => w.Write(idx));
        }
    }

    [HarmonyPatch(typeof(CBuilding), nameof(CBuilding.Place))]
    internal static class CapturePlaceBuilding
    {
        private static void Prefix(Vector3 position, int type, int owner, int level, float angle, float condition, bool newlycreated, float constructiontimer, bool pay)
        {
            if (!GuestRoute.Active || !newlycreated || owner != Mp.GuestAlliance) return;
            GuestActions.Send(ActionKind.PlaceBuilding, w =>
            {
                w.Write(position); w.Write(type); w.Write(level); w.Write(angle); w.Write(condition); w.Write(constructiontimer); w.Write(pay);
            });
        }
    }
}
