using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Patches
{
    /// <summary>
    /// The game opens scenelog.dat with an exclusive StreamWriter on first log line, so a second copy
    /// running from the same folder would throw on every DebugOwnLog call. Give each instance its own file.
    /// </summary>
    [HarmonyPatch(typeof(GameVars), nameof(GameVars.DebugOwnLog))]
    internal static class SceneLogPerInstance
    {
        private static void Prefix()
        {
            if (GameVars.debug_ownlogfile1 != null || !GameVars.debug_activateownlog) return;
            if (string.IsNullOrEmpty(Plugin.InstanceId)) return;
            GameVars.debug_ownlogfile1 = new StreamWriter("scenelog_" + Plugin.InstanceId + ".dat");
        }
    }

    /// <summary>Lab instances run windowed at a fixed size regardless of the user's display settings.</summary>
    [HarmonyPatch(typeof(Options), nameof(Options.LoadConfigs))]
    internal static class LabWindowed
    {
        private static void Postfix()
        {
            if (!Plugin.LabMode) return;
            Options.fullscreen = false;
            Screen.SetResolution(Plugin.LabWidth, Plugin.LabHeight, false);
        }
    }

    [HarmonyPatch(typeof(Options), nameof(Options.SetResolution))]
    internal static class LabWindowedMenu
    {
        private static bool Prefix()
        {
            if (!Plugin.LabMode) return true;
            Screen.SetResolution(Plugin.LabWidth, Plugin.LabHeight, false);
            return false;
        }
    }

    /// <summary>Battle launches save the campaign into the autosave folder; lab instances use their own.</summary>
    [HarmonyPatch(typeof(Saving), nameof(Saving.SetAutosaveFolder))]
    internal static class LabAutosaveFolder
    {
        private static void Postfix()
        {
            if (Plugin.LabMode && Saving.latestfolder != null && Saving.latestfolder.StartsWith("Save"))
                Saving.latestfolder = "GTLive_LabAuto_" + Plugin.InstanceId;
        }
    }

    /// <summary>Lab instances must never overwrite the user's Config files or autosaves.</summary>
    [HarmonyPatch]
    internal static class LabNoUserFileWrites
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Options), nameof(Options.SaveConfigs));
            yield return AccessTools.Method(typeof(Filter), nameof(Filter.SaveFilters));
            yield return AccessTools.Method(typeof(GameVars), nameof(GameVars.SaveKeyCodes));
            yield return AccessTools.Method(typeof(ImportExportUnitData), nameof(ImportExportUnitData.SaveLastGamePlayed));
            yield return AccessTools.Method(typeof(GameVars), nameof(GameVars.CheckAutosave));
        }

        private static bool Prefix() => !Plugin.LabMode;
    }
}
