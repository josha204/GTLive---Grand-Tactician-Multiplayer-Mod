using GTLive.Sync;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Patches
{
    // ---------------------------------------------------------------------------------------------------
    // Guest: the host is the only place where combat, AI and autosaves happen.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Raids, skirmishes, retreats and battles all start in CheckEnemyContactCampaign; only the host runs it.</summary>
    [HarmonyPatch(typeof(Regiment), "CheckEnemyContactCampaign")]
    internal static class GuestNoContact
    {
        private static bool Prefix() => !(Mp.IsGuest && Mp.CampaignRules);
    }

    [HarmonyPatch(typeof(Regiment), "CheckIfJoiningOwnUnit")]
    internal static class GuestNoJoining
    {
        private static bool Prefix() => !(Mp.IsGuest && Mp.CampaignRules);
    }

    /// <summary>The campaign AI brain (moves for AI factions). The host runs it for the European powers; the guest never does.</summary>
    [HarmonyPatch(typeof(AICampaign), "UpdateUnitAI")]
    internal static class GuestNoUnitAI
    {
        private static bool Prefix() => !(Mp.IsGuest && Mp.CampaignRules);
    }

    /// <summary>
    /// AICampaign.dat stores per-AI-faction state by list index. In a session the faction list no longer contains
    /// the human alliances, so the saved indices don't line up and Load throws part-way. The exception would also
    /// abort CampaignController's load frame before it loads the weather, so swallow it after logging.
    /// </summary>
    [HarmonyPatch(typeof(AICampaign), nameof(AICampaign.Load))]
    internal static class AiLoadTolerant
    {
        private static System.Exception Finalizer(System.Exception __exception)
        {
            if (__exception == null || !Mp.Active) return __exception;
            Plugin.Log.LogInfo("AICampaign.Load stopped early in a session (AI faction list differs from the save): " + __exception.Message);
            return null;
        }
    }

    /// <summary>The guest never creates AI factions; code that asks for one gets "none" instead of a null crash.</summary>
    [HarmonyPatch]
    internal static class NoAiFactionsSafe
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.Inner(typeof(AICampaign), "AIFaction"), "GetAIFactionIDFromAllianceID");

        private static bool Prefix(ref int __result)
        {
            if (Traverse.Create(typeof(AICampaign)).Field("aifaction").GetValue() != null) return true;
            __result = -1;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameVars), nameof(GameVars.CheckAutosave))]
    internal static class GuestNoAutosave
    {
        private static bool Prefix() => !Mp.IsGuest;
    }

    // ---------------------------------------------------------------------------------------------------
    // Shared clock: only explicit pause/speed input changes it; the game's automatic pauses are ignored.
    // ---------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(BattleUI), "CheckCommonKeys")]
    internal static class SpeedHotkeys
    {
        private static void Prefix() => ClockSync.InKeyHandler = true;
        private static void Finalizer() => ClockSync.InKeyHandler = false;
    }

    [HarmonyPatch(typeof(GameVars), nameof(GameVars.SetGameSpeed))]
    internal static class SharedSpeed
    {
        public static int TimeLogicCalls, Blocked;

        private static bool Prefix(float newspeed, bool manualset)
        {
            if (Mp.Applying || !Session.IsLive) return true;
            bool battle = Battle.BattleSync.ClockActive;
            float speed = newspeed < 0f ? GameVars.gamespeed : newspeed;
            // a shared battle that isn't running yet (the other player is still loading) stays paused
            if (!GameApi.InCampaignScene && !battle) return !Battle.BattleRules.InMpBattle || speed <= 0f;
            if (battle && Battle.BattleSync.InGameTimeLogic)
            {
                // host: the battle's own clock logic (night skip, morning pause, battle over) is shared as-is
                TimeLogicCalls++;
                ClockSync.SetShared(speed <= 0f, speed);
                return true;
            }
            bool user = manualset || (ClockSync.InKeyHandler && SpeedKeyPressed());
            if (!user) { Blocked++; return false; }
            if (battle && GameApi.BUI != null && GameApi.BUI.deploymentmodeactive)
            {
                // during deployment the clock stays stopped; pressing play means "ready" (see SharedDeployment)
                if (speed > 0f) Battle.BattleSync.PreferredSpeed = speed;
                return false;
            }
            if (speed <= 0f) ClockSync.LocalInput(true, ClockSync.SharedSpeed);
            else ClockSync.LocalInput(false, speed);
            return false;
        }

        private static bool SpeedKeyPressed()
        {
            int[] keys =
            {
                GameVars.key_pausegame, GameVars.key_increasespeed, GameVars.key_decreasespeed,
                GameVars.key_speed0, GameVars.key_speed1, GameVars.key_speed2, GameVars.key_speed3, GameVars.key_speed4,
            };
            foreach (var k in keys)
                if (k >= 0 && k < GameVars.keycodes.Length && Input.GetKeyDown(GameVars.keycodes[k].keycode)) return true;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Host: battles between the two players.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// In single player the battle panel asks the player to fight, autoresolve or withdraw while the game is paused.
    /// With "Autoresolve" the host answers it immediately so the live campaign keeps running for both players.
    /// </summary>
    [HarmonyPatch(typeof(BattleUI), nameof(BattleUI.UpdateBattlePanel))]
    internal static class HostBattlePanel
    {
        private static void Prefix(BattleUI __instance)
        {
            if (!Mp.IsHost || !Session.IsLive || !GameApi.InCampaignScene) return;
            if (__instance.battlepanelshown != 1 || Battle.BattleDecision.Pending) return;
            if (Session.Battles == BattleMode.Autoresolve)
            {
                __instance.SetBattlePanelShownState(5);
                Session.SendNotice("A battle broke out and is being autoresolved on the campaign map.");
                return;
            }
            // both players answer the panel; battles that don't involve the guest are the host's call alone
            if (!Battle.BattleDecision.HostBegin())
            {
                ClockSync.SetShared(true, ClockSync.SharedSpeed);
                Session.SendNotice("A battle broke out. The host is deciding how to fight it.");
            }
        }
    }
}
