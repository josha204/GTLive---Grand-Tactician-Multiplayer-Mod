using System;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Patches
{
    /// <summary>
    /// A bug in the game itself: a siege-style engagement on the campaign map (Autocalc.UpdateSiegeCombat) runs a
    /// combat cycle that can end the battle and destroy the engagement (FinishLandBattle -> DestroyImmediate),
    /// then checks the force balance and calls FinishLandBattle a second time on the destroyed engagement, which
    /// throws a NullReferenceException before it changes anything. Skip that second call.
    /// If the method ever fails for another reason, the battle's data is logged for the bug report.
    /// </summary>
    [HarmonyPatch(typeof(Autocalc), "FinishLandBattle")]
    internal static class FinishLandBattleTwice
    {
        public static int Skipped;

        private static bool Prefix(Autocalc __instance)
        {
            if (__instance != null) return true;
            Skipped++;
            return false;
        }

        private static Exception Finalizer(Exception __exception, Autocalc __instance, int endbattle, int winningside)
        {
            if (__exception == null) return null;
            string state;
            try { state = Describe(__instance, endbattle, winningside); }
            catch (Exception e) { state = "(could not describe: " + e.Message + ")"; }
            Plugin.Log.LogError($"Autocalc.FinishLandBattle failed in a {(Mp.Active ? "session" : "single player")} game: {__exception}\n{state}");
            return __exception;
        }

        public static string Describe(Autocalc a, int endbattle, int winningside)
        {
            if (a == null) return "the engagement was already destroyed";
            var sb = new StringBuilder();
            var mon = a.GetComponent<BattleMonument>();
            sb.Append($"type={a.type} endbattle={endbattle} winningside={winningside} monument={(mon != null ? mon.battlename : "MISSING")}");
            sb.Append($" alliance=[{(a.alliance == null ? "null" : string.Join(",", a.alliance))}] playeralliance={GameVars.playeralliance}");
            if (a.envolvedunits == null) { sb.Append(" envolvedunits=null"); return sb.ToString(); }
            for (int s = 0; s < a.envolvedunits.Length; s++)
            {
                var e = a.envolvedunits[s];
                if (e == null) { sb.Append($"\n side {s}: null"); continue; }
                sb.Append($"\n side {s}: top={e.topunits?.Count} arrived={e.topunitsarrived?.Count} single={e.singleunitlist?.Count} arrival={e.arrivaltime?.Count} topref={e.topunitref?.Count}");
                if (e.topunits != null)
                    for (int i = 0; i < e.topunits.Count; i++)
                        sb.Append($"\n   top[{i}] {Desc(e.topunits[i])}");
                if (e.singleunitlist != null)
                    for (int i = 0; i < e.singleunitlist.Count; i++)
                    {
                        var r = e.singleunitlist[i];
                        var t = e.topunitref != null && i < e.topunitref.Count ? e.topunitref[i] : null;
                        bool odd = r == null || r.losses == null || t == null || r.commander < 0 || r.commander >= GameVars.commander.Count
                                   || r.state < 0 || r.state >= GameVars.nation.Length;
                        if (odd) sb.Append($"\n   single[{i}] {Desc(r)} topref={Desc(t)}");
                    }
            }
            return sb.ToString();
        }

        private static string Desc(Regiment r)
        {
            if (ReferenceEquals(r, null)) return "null";
            if (r == null) return "destroyed";
            return $"{r.name}#{r.unitid} all={r.alliance} typ={r.unittyp} cmd={r.commander} state={r.state} losses={(r.losses == null ? "null" : "ok")} garrison={(r.garrisonreference != null)} active={r.gameObject.activeInHierarchy}";
        }
    }

    /// <summary>
    /// Unloading a campaign scene (loading a save, or the host reloading at session start) destroys the
    /// "ProceduralObjects" folder while units still update for that frame; a unit drawing telegraph lines then
    /// throws. Harmless; keep it out of the log.
    /// </summary>
    [HarmonyPatch(typeof(Regiment), "UpdateTelegraphLines")]
    internal static class TelegraphLinesTeardown
    {
        private static Exception Finalizer(Exception __exception) =>
            __exception is NullReferenceException && GameObject.Find("ProceduralObjects") == null ? null : __exception;
    }
}
