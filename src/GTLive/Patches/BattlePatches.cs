using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GTLive.Battle;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Patches
{
    /// <summary>
    /// Shared 3D battles. The host runs the real battle; the guest's game renders the same battle, moves units
    /// along the routes the host reports and lets its own player give orders, but everything that decides an
    /// outcome (AI, morale, casualties, routs, melee, reactions to the enemy, the end of the battle) only runs
    /// on the host and reaches the guest as state.
    /// </summary>
    internal static class BattlePatches
    {
        public static readonly List<string> Report = new List<string>();

        /// <summary>True on the guest inside a shared battle, unless the host's state is being applied.</summary>
        public static bool GuestSkipping => Mp.IsGuest && !Mp.Applying && BattleRules.InMpBattle;

        // Decided by the host only. Skipped on the guest; the result arrives as state or as orders.
        private static readonly (Type type, string name)[] GuestHostOnly =
        {
            (typeof(Regiment), "UpdateMorale"),
            (typeof(Regiment), "CheckRoutUpdate2"),
            (typeof(Regiment), "Rout"),
            (typeof(Regiment), "CheckFallbackDueToEnemyClose"),
            (typeof(Regiment), "UpdateSurrender"),
            (typeof(Regiment), "CheckEnemyContact"),
            (typeof(Regiment), "UpdateFatigue"),
            (typeof(Regiment), "UpdateRetreatTarget"),
            (typeof(Regiment), "UpdateChargeTarget"),
            (typeof(Regiment), "MoveNonAIUnits"),
            (typeof(Regiment), "UpdateSafetyZone"),
            (typeof(Regiment), "UpdateRallyZone"),
            (typeof(Regiment), "CheckNewTargetsOfSkirmishers"),
            (typeof(Regiment), "CheckSkirmisherParentStatus"),
            (typeof(Regiment), "CheckForUnitTargets"),
            (typeof(Regiment), "CheckIfFinalWaypointBlocked"),
            (typeof(Regiment), "UpdateUnitLink"),
            (typeof(Regiment), "CheckForReattachmentOrder"),
            (typeof(Regiment), "CheckForTakingGuns"),
            (typeof(Regiment), "UpdateAutoTargets"),
            (typeof(Regiment), "CheckAllUnitsRouted"),
            (typeof(Regiment), "CheckScoutMovingTargets"),
            (typeof(Regiment), "SufferCasualties"),
            (typeof(Building), "damage"),
            (typeof(AIBattle), "Update"),
            (typeof(AIBattle), "UpdateAITasks"),
            (typeof(AIBattle), "AICheckInitialPontoonConstruction"),
            (typeof(AIBattle), "CheckForPontoonConstruction"),
            (typeof(BattleUnits), "CheckAISurrender"),
            (typeof(BattleUnits), "CheckAINightWithdrawal"),
            (typeof(BattleUnits), "CheckBattleEnd"),
            (typeof(BattleUnits), "DoPlacementAIUnitsWithinDeploymentzoneNew"),
            (typeof(BattleUnits), "DoPlacementAIUnitsWithinDeploymentzone"),
            (typeof(GameVars), "CheckAutosaveBattle"),
            // reinforcements arrive when the host says so (BattleSync.OnArrival)
            (typeof(BattleUnits), "CheckReinforcementArrivals"),
            // construction progress comes from the host (BattleEngineering)
            (typeof(Pontoons), "IncreaseConstruction"),
            (typeof(ObstacleLine), "IncreaseConstruction"),
        };

        // Never on the guest, not even while the host's state is applied (they roll dice).
        private static readonly (Type type, string name)[] GuestNever =
        {
            (typeof(Regiment), "UpdateCommanderCasualtyState"),
            (typeof(Regiment), "Surrender"),
        };

        // What a player changes about a unit when an order takes effect; the guest reports these to the host.
        private static readonly (Type type, string name)[] OrderEffects =
        {
            (typeof(Regiment), "RegimentSetPath"),
            (typeof(Regiment), "StopRegiment"),
            (typeof(Regiment), "SetOrderStatus"),
            (typeof(Regiment), "SetCombatBehaviorImmediate"),
            (typeof(Regiment), "SetAIStance"),
            (typeof(Regiment), "ChangeRegimentFormation"),
            (typeof(Regiment), "SetMovementMode"),
            (typeof(Regiment), "RebuildAllManualWaypoints"),
        };

        // UI order entry points: locked while the local player only spectates their army.
        private static readonly (Type type, string name)[] UiOrders =
        {
            (typeof(BattleUnits), "SetWaypoint"),
            (typeof(BattleUnits), "SetFormation"),
            (typeof(BattleUnits), "ChangeCombatBehavior"),
            (typeof(BattleUnits), "ChangeStance"),
            (typeof(BattleUnits), "OrderDetachingFromGroup"),
            (typeof(BattleUnits), "StopAllUnits"),
            (typeof(BattleUnits), "MountUnits"),
            (typeof(BattleUnits), "SetGroupFormation"),
        };

        public static void Apply(Harmony h)
        {
            foreach (var (t, name) in GuestHostOnly) PatchAll(h, t, name, "Skip");
            foreach (var (t, name) in GuestNever) PatchAll(h, t, name, "Never");
            foreach (var (t, name) in OrderEffects) PatchAll(h, t, name, null, nameof(MarkOrderEffect));
            foreach (var (t, name) in UiOrders) PatchAll(h, t, name, null, nameof(SpectatorLock));
            foreach (var line in Report) Plugin.Log.LogInfo("battle-patches: " + line);
        }

        private static void PatchAll(Harmony h, Type t, string name, string skipKind, string prefixName = null)
        {
            var methods = t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters).ToList();
            if (methods.Count == 0) { Report.Add($"MISSING {t.Name}.{name}"); return; }
            foreach (var m in methods)
            {
                try
                {
                    string pre = prefixName;
                    if (pre == null)
                    {
                        var rt = m.ReturnType;
                        string suffix = rt == typeof(void) ? "Void" : rt == typeof(bool) ? "Bool" : rt == typeof(int) ? "Int" : rt == typeof(float) ? "Float" : null;
                        if (suffix == null) { Report.Add($"UNSUPPORTED return type {t.Name}.{name}: {rt.Name}"); continue; }
                        pre = skipKind + suffix;
                    }
                    h.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(BattlePatches), pre)));
                }
                catch (Exception e)
                {
                    Report.Add($"FAILED {t.Name}.{name}: {e.Message}");
                }
            }
        }

        // ---- guest: host-only logic --------------------------------------------------------------------

        private static bool SkipVoid() => !GuestSkipping;
        private static bool SkipBool(ref bool __result) { if (!GuestSkipping) return true; __result = false; return false; }
        private static bool SkipInt(ref int __result) { if (!GuestSkipping) return true; __result = 0; return false; }
        private static bool SkipFloat(ref float __result) { if (!GuestSkipping) return true; __result = 0f; return false; }

        private static bool NeverOnGuest => Mp.IsGuest && BattleRules.InMpBattle;
        private static bool NeverVoid() => !NeverOnGuest;
        private static bool NeverBool(ref bool __result) { if (!NeverOnGuest) return true; __result = false; return false; }
        private static bool NeverInt(ref int __result) { if (!NeverOnGuest) return true; __result = 0; return false; }
        private static bool NeverFloat(ref float __result) { if (!NeverOnGuest) return true; __result = 0f; return false; }

        // ---- guest: which of its own units did the player just re-order -------------------------------

        private static void MarkOrderEffect(object __instance)
        {
            if (!Mp.IsGuest || Mp.Applying || !(BattleSync.InUi || BattleSync.InOrderProcessing)) return;
            if (__instance is Regiment r && BattleRules.InMpBattle) BattleSync.MarkOwnOrder(r);
        }

        // ---- spectating: your army is the AI's until you take command ----------------------------------

        private static bool SpectatorLock(object[] __args)
        {
            if (!BattleSync.InUi || Mp.Applying || !BattleRules.InMpBattle || BattleRules.LocalCommands) return true;
            foreach (var a in __args)
            {
                int alliance = AllianceOf(a);
                if (alliance == int.MinValue) continue;
                if (alliance == Mp.LocalAlliance)
                {
                    BattleSync.SpectatorBlocked();
                    return false;
                }
                return true;
            }
            return true;
        }

        private static int AllianceOf(object a)
        {
            switch (a)
            {
                case Regiment r: return r != null ? r.alliance : int.MinValue;
                case GameObject go:
                    if (go == null) return int.MinValue;
                    var reg = go.GetComponent<Regiment>();
                    return reg != null ? reg.alliance : int.MinValue;
                case IEnumerable list when !(a is string):
                    foreach (var x in list)
                    {
                        int v = AllianceOf(x);
                        if (v != int.MinValue) return v;
                    }
                    return int.MinValue;
            }
            return int.MinValue;
        }
    }

    // -------------------------------------------------------------------------------------------------------
    // Input context
    // -------------------------------------------------------------------------------------------------------

    /// <summary>Clicks, hotkeys and panel buttons are all handled inside BattleUI.Update.</summary>
    [HarmonyPatch(typeof(BattleUI), "Update")]
    internal static class BattleUiInput
    {
        public static int Calls;

        private static void Prefix()
        {
            Calls++;
            BattleSync.InUi = true;
        }

        private static void Finalizer() => BattleSync.InUi = false;
    }

    [HarmonyPatch(typeof(UnitPanel), "Update")]
    internal static class UnitPanelInput
    {
        private static void Prefix() => BattleSync.InUi = true;
        private static void Finalizer() => BattleSync.InUi = false;
    }

    /// <summary>A courier arriving executes a queued order; on the guest those effects are the player's orders.</summary>
    [HarmonyPatch(typeof(Regiment), "ProcessOrders")]
    internal static class OrderProcessing
    {
        private static void Prefix() => BattleSync.InOrderProcessing = true;
        private static void Finalizer() => BattleSync.InOrderProcessing = false;
    }

    /// <summary>Host: the battle's own time logic (night skip, morning pause, end-of-battle pause) drives the shared clock.</summary>
    [HarmonyPatch(typeof(BattleUnits), "Update")]
    internal static class HostBattleTimeLogic
    {
        public static int Calls;

        private static void Prefix()
        {
            Calls++;
            BattleSync.InGameTimeLogic = Mp.IsHost && BattleRules.InMpBattle;
        }

        private static void Finalizer() => BattleSync.InGameTimeLogic = false;
    }

    // -------------------------------------------------------------------------------------------------------
    // AI control per side
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The battle AI runs a full army (isplayeraiorfeud 0) or only helps a human (1). In a shared battle that
    /// follows who commands each side rather than which side is the local player's.
    /// </summary>
    [HarmonyPatch(typeof(AIBattle), nameof(AIBattle.SetSide))]
    internal static class AiSidePerCommander
    {
        private static void Postfix(AIBattle __instance, bool isfeud)
        {
            if (isfeud || GameVars.debug_allarmiesai || !BattleRules.InMpBattle) return;
            var bu = GameApi.BU;
            if (bu?.alliance == null || __instance.sideofai < 0 || __instance.sideofai >= bu.alliance.Length) return;
            __instance.isplayeraiorfeud = BattleRules.Commanding(bu.alliance[__instance.sideofai]) ? 1 : 0;
        }
    }

    /// <summary>
    /// Host: giving a unit an AI stance hands it to the side's helper AI. The game only does that for the local
    /// player's side; do the same for the guest's units on the guest's AI.
    /// </summary>
    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.ChangeStance))]
    internal static class GuestAiStancePool
    {
        private static void Postfix(GameObject selectedunit, int newstance, bool overwriteaigroups)
        {
            if (!Mp.IsHost || !BattleRules.InMpBattle || selectedunit == null) return;
            var r = selectedunit.GetComponent<Regiment>();
            if (r == null || r.alliance != Mp.GuestAlliance || !BattleRules.GuestCommands) return;
            var ai = BattleSync.AiFor(r.alliance);
            if (ai == null) return;
            if (newstance > 0) ai.AddUnitToPool(r, -1, forcecreatingnewchain: false, overwriteaigroups);
            else ai.RemoveUnitFromPool(r);
        }
    }

    /// <summary>
    /// A path whose courier hasn't arrived waits (status 0); the game force-starts such paths when it can't find
    /// the courier. The guest's couriers only exist on the guest, so the host must not start the guest's units
    /// early, and the guest must not start the host's.
    /// </summary>
    [HarmonyPatch(typeof(Regiment), "CheckNonArrivedCouriers")]
    internal static class CouriersStayWithTheirPlayer
    {
        private static bool Prefix(Regiment __instance)
        {
            if (!BattleRules.InMpBattle) return true;
            bool guestOrdered = __instance.alliance == Mp.GuestAlliance && BattleRules.GuestCommands;
            return Mp.IsHost ? !guestOrdered : guestOrdered;
        }
    }

    // -------------------------------------------------------------------------------------------------------
    // Guest: time and deployment come from the host
    // -------------------------------------------------------------------------------------------------------

    /// <summary>Guest: night, end of day and the morning deployment are the host's; only keep the battle time variables current.</summary>
    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.CheckTimeIssues))]
    internal static class GuestBattleTime
    {
        private static bool Prefix(BattleUnits __instance)
        {
            if (!Mp.IsGuest || !BattleRules.InMpBattle) return true;
            __instance.SetTimeVariables();
            return false;
        }
    }

    /// <summary>Ending deployment means "I'm ready". The battle starts when every commanded side is ready.</summary>
    [HarmonyPatch(typeof(BattleUI), nameof(BattleUI.SetActiveDeploymentPhase))]
    internal static class SharedDeployment
    {
        private static bool Prefix(BattleUI __instance, bool active)
        {
            if (active || Mp.Applying || !BattleRules.InMpBattle || !__instance.deploymentmodeactive) return true;
            // the scene and the save loader toggle deployment while setting up; that's not the player
            if (!BattleLink.Active || GameVars.frame < 60) return true;
            BattleSync.LocalDeployReady();
            return false;
        }
    }

    /// <summary>
    /// Guest army orders. The confirmation prompts (retreat 3, surrender 5) and "never mind" (-1) run locally;
    /// a confirmed order goes to the host, which carries it out the way the AI's own retreats and surrenders
    /// work. The outcome of the battle comes from the host (BattleSync mirrors it from the guest's side).
    /// </summary>
    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.SetEndOfBattle))]
    internal static class GuestArmyOrders
    {
        private static bool Prefix(BattleUnits __instance, int action)
        {
            if (!Mp.IsGuest || Mp.Applying || !BattleRules.InMpBattle) return true;
            switch (action)
            {
                case -1:
                case 3:
                case 5:
                    return BattleRules.LocalCommands || action == -1;
                case 4:
                    // retreat confirmed at night
                    BattleSync.SendArmyOrder(BattleSync.ArmyOrder.NightWithdraw);
                    return false;
                case 11:
                    if (__instance.endbattle == 5) BattleSync.SendArmyOrder(BattleSync.ArmyOrder.Surrender);
                    else if (BattleSync.InUi) Session.Notice("Back to the campaign when " + Session.RemoteName + " leaves the battlefield.");
                    return false;
                case 10:
                    // the cease-fire prompt; confirming it sets triggerendofday, which BattleSync turns into a proposal
                    return BattleRules.LocalCommands;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Host: a battle's outcome (won, lost, withdrawn, surrendered) pauses the game, as in single player, and
    /// that pause is shared. Confirmation prompts and retreat timers don't stop the other player's clock.
    /// </summary>
    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.SetEndOfBattle))]
    internal static class HostBattleOutcomePause
    {
        private static void Prefix(int action, out bool __state)
        {
            __state = BattleSync.InGameTimeLogic;
            bool final = action == 0 || action == 1 || action == 2 || action == 6 || action == 7 || action == 13;
            if (final && Mp.IsHost && BattleRules.InMpBattle) BattleSync.InGameTimeLogic = true;
        }

        private static void Finalizer(bool __state) => BattleSync.InGameTimeLogic = __state;
    }

    /// <summary>
    /// Leaving a battle scene, units still update for a frame while their fire-arc meshes are already gone.
    /// Harmless, but it fills the log that players send with bug reports.
    /// </summary>
    [HarmonyPatch(typeof(Regiment), "UpdateFireArc")]
    internal static class FireArcTeardown
    {
        private static System.Exception Finalizer(System.Exception __exception) =>
            __exception is NullReferenceException && Mp.Active
            && (BattleController.quitbattlescene || SceneManagement.currentoperation != 3 || GameObject.Find("ProceduralObjects") == null)
                ? null : __exception;
    }

    /// <summary>Host: a reinforcement group marches on; the guest's game brings in the same group at the same entry point.</summary>
    [HarmonyPatch(typeof(BattleUnits), "SetGroupArrival")]
    internal static class HostArrivals
    {
        private static void Prefix(BattleUnits __instance, int groupidwithinscheduledarrival, bool allarriveimmediately)
        {
            if (Mp.IsHost && BattleRules.InMpBattle) BattleSync.HostArrival(__instance, groupidwithinscheduledarrival, allarriveimmediately);
        }
    }

    /// <summary>
    /// Engineers on a pontoon site (right-click with an engineer unit, or a click in deployment that builds it with
    /// engineering points). Spectators can't; the guest's orders go to the host.
    /// </summary>
    [HarmonyPatch(typeof(BattleUI), nameof(BattleUI.CheckPontoonConstruction))]
    internal static class PontoonOrders
    {
        private static bool Prefix(bool isui, ref bool __result)
        {
            if (!isui || Mp.Applying || !BattleRules.InMpBattle || BattleRules.LocalCommands) return true;
            __result = false;
            return false;
        }

        private static void Postfix(BattleUI __instance, Pontoons pontoon, GameObject manualunit, bool isui, bool __result)
        {
            if (!__result || !isui || Mp.Applying || !Mp.IsGuest || !BattleRules.InMpBattle) return;
            var p = pontoon;
            if (p == null && manualunit != null)
            {
                var reg = manualunit.GetComponent<Regiment>();
                if (reg?.pontoonconstruction != null) p = reg.pontoonconstruction.GetComponent<Pontoons>();
            }
            if (p == null)
            {
                var hit = Traverse.Create(__instance).Field("mousehit").GetValue<RaycastHit>();
                if (hit.collider != null) p = hit.collider.gameObject.GetComponent<Pontoons>();
            }
            if (p != null) BattleEngineering.SendGuestPontoon(p);
        }
    }

    /// <summary>Obstacle lines (trenches, barricades): spectators can't draw them; the guest's go to the host when finished.</summary>
    [HarmonyPatch(typeof(BattleUI), nameof(BattleUI.ActivateObstacleConstruction))]
    internal static class ObstacleButtons
    {
        private static bool Prefix()
        {
            if (Mp.Applying || !BattleRules.InMpBattle || BattleRules.LocalCommands) return true;
            BattleSync.SpectatorBlocked();
            return false;
        }

        private static void Postfix()
        {
            if (Mp.IsGuest && !Mp.Applying && BattleRules.InMpBattle) BattleEngineering.SendNewGuestLines();
        }
    }

    [HarmonyPatch(typeof(BattleUI), nameof(BattleUI.EndManualObstacleConstruction))]
    internal static class ObstacleFinished
    {
        private static void Postfix()
        {
            if (Mp.IsGuest && !Mp.Applying && BattleRules.InMpBattle) BattleEngineering.SendNewGuestLines();
        }
    }

    /// <summary>Host: a cease fire the host's player confirmed waits for the guest's agreement before the day ends.</summary>
    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.CheckTimeIssues))]
    internal static class HostCeaseFire
    {
        private static void Prefix(BattleUnits __instance)
        {
            if (!Mp.IsHost || !BattleRules.InMpBattle || !__instance.triggerendofday) return;
            if (!BattleCeaseFire.HostWantsToEndDay()) __instance.triggerendofday = false;
        }
    }

    /// <summary>Guest: a confirmed daytime retreat starts the retreat timer; the host runs it.</summary>
    [HarmonyPatch(typeof(TimePanel), nameof(TimePanel.SetRetreatTimer))]
    internal static class GuestRetreat
    {
        private static bool Prefix(int alliance)
        {
            if (!Mp.IsGuest || Mp.Applying || !BattleRules.InMpBattle) return true;
            if (alliance == Mp.GuestAlliance) BattleSync.SendArmyOrder(BattleSync.ArmyOrder.Retreat);
            return false;
        }
    }

    // -------------------------------------------------------------------------------------------------------
    // Guest orders the host has to carry out itself (they choose targets, which only the host can do)
    // -------------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(Regiment), nameof(Regiment.AssignTarget))]
    internal static class TargetOrders
    {
        private static bool Prefix(Regiment __instance, GameObject targetunit, bool uiactive, float shorterfirerange, int manualtargetedenemyunittype)
        {
            if (Mp.Applying || !BattleRules.InMpBattle) return true;
            bool fromUi = uiactive && BattleSync.InUi && __instance.alliance == Mp.LocalAlliance;
            if (fromUi && !BattleRules.LocalCommands) { BattleSync.SpectatorBlocked(); return false; }
            if (!Mp.IsGuest) return true;
            if (fromUi) BattleSync.SendTarget(__instance, targetunit, shorterfirerange, manualtargetedenemyunittype);
            // the guest's own automatic target picking is replaced by the host's
            return false;
        }
    }

    [HarmonyPatch(typeof(Regiment), nameof(Regiment.AssignChargeTarget))]
    internal static class ChargeOrders
    {
        private static bool Prefix(Regiment __instance, GameObject newchargetarget, bool usechargeorder)
        {
            if (Mp.Applying || !BattleRules.InMpBattle) return true;
            bool fromUi = BattleSync.InUi && __instance.alliance == Mp.LocalAlliance;
            if (fromUi && !BattleRules.LocalCommands) { BattleSync.SpectatorBlocked(); return false; }
            if (!Mp.IsGuest) return true;
            if (fromUi) BattleSync.SendCharge(__instance, newchargetarget, usechargeorder);
            return false;
        }
    }

    [HarmonyPatch(typeof(BattleUI), "AssignUnitToEnterBuilding")]
    internal static class BuildingOrders
    {
        private static bool Prefix(GameObject unit, GameObject building)
        {
            if (Mp.Applying || !BattleRules.InMpBattle || unit == null) return true;
            var r = unit.GetComponent<Regiment>();
            if (r == null || r.alliance != Mp.LocalAlliance) return true;
            if (!BattleRules.LocalCommands) { BattleSync.SpectatorBlocked(); return false; }
            if (!Mp.IsGuest) return true;
            BattleSync.SendEnterBuilding(r, building);
            return false;
        }
    }
}
