using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace GTLive.Patches
{
    /// <summary>
    /// The campaign AI decides "is this the player's alliance?" with comparisons against GameVars.playeralliance.
    /// With two humans both alliances must count as the player's. This transpiler rewrites those comparisons in the
    /// AI decision methods so that "x == playeralliance" becomes "x == Mp.MatchHuman(x)", which is unchanged in
    /// single player and true for either human alliance in a session. UI and message code is left alone so each
    /// player still only sees their own side's notifications.
    /// </summary>
    internal static class HumanPatches
    {
        private static readonly FieldInfo PlayerAlliance = AccessTools.Field(typeof(GameVars), nameof(GameVars.playeralliance));
        private static readonly MethodInfo MatchHuman = AccessTools.Method(typeof(Mp), nameof(Mp.MatchHuman));
        private static readonly MethodInfo MatchCommander = AccessTools.Method(typeof(Battle.BattleRules), nameof(Battle.BattleRules.MatchCommander));

        /// <summary>Human: "is this side a human player?" (difficulty bonuses). Commander: "is a human ordering this side?" (AI control).</summary>
        private enum Rule { Human, Commander }

        // method -> which playeralliance reads (1-based, IL order) to rewrite and how; null = all of them, Human rule
        private static readonly Dictionary<MethodBase, Dictionary<int, Rule>> Targets = new Dictionary<MethodBase, Dictionary<int, Rule>>();
        private static readonly Dictionary<MethodBase, Rule> AllRule = new Dictionary<MethodBase, Rule>();
        public static readonly List<string> Report = new List<string>();

        public static void Apply(Harmony harmony)
        {
            // Every AI routine in AICampaign (and its nested types) is a decision for "the other side".
            foreach (var t in new[] { typeof(AICampaign) }.Concat(typeof(AICampaign).GetNestedTypes(AccessTools.all)))
                foreach (var m in t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                    if (!m.IsAbstract && !m.ContainsGenericParameters && ReadsPlayerAlliance(m))
                        Targets[m] = null;

            // In these AICampaign methods the first reads only decide whether to show the player a message.
            Add(typeof(AICampaign), "UpdateFinancialAI", 3);                // 1,2 = bond messages

            Add(typeof(BattleUnits), "UpdateCommanderHistories", 4);       // 1-3 = return-from-WIA/POW messages, 4 = AI promotions
            Add(typeof(Policies), "CheckAIPolicyChange");                   // AI policy choice
            Add(typeof(GameVars.Commander), "CheckActivation");             // historical auto-promotion of AI-side generals
            Add(typeof(Regiment), "UpdateMorale");                          // AI morale bonuses (all five sites)
            Add(typeof(Regiment), "UpdateFatigue");                         // AI fatigue bonus
            Add(typeof(Regiment), "GetUnbearableLossesTrigger");            // AI loss tolerance bonus
            Add(typeof(Regiment), "RegimentMove", 2);                       // AI fallback speed; other sites are UI

            // ---- shared 3D battles: no difficulty bonus for either human side ----
            Add(typeof(Regiment), "IncreaseExperience", 1);                 // AI experience bonus
            Add(typeof(Regiment), "SufferCasualties", 1);                   // difficulty casualty modifier

            // ---- shared 3D battles: AI control follows who commands each side, not playeralliance ----
            AddCommander(typeof(BattleUnits), "AllocateUnitsAndObjectivesToAI");     // which side's groups go to the battle AI
            AddCommander(typeof(BattleUnits), "CheckAISurrender");
            AddCommander(typeof(BattleUnits), "CheckAINightWithdrawal", 1);
            AddCommander(typeof(AIBattle), "AICheckInitialPontoonConstruction");
            AddCommander(typeof(Regiment), "Initialize", 1, 2);                     // AI route habits (hills, rails)
            AddCommander(typeof(Regiment), "MoveNonAIUnits", 1);                    // AI habit: link loose units to parents
            AddCommander(typeof(Regiment), "CheckEnemyContact", 1, 2, 3);          // AI reactions to contact
            AddCommander(typeof(Regiment), "RegimentSetPath", 1);                   // AI road preference
            AddCommander(typeof(Regiment), "RegimentMove", 1);                      // human units wait for others to pass
            AddCommander(typeof(Regiment), "UpdateUnitLink", 1);
            AddCommander(typeof(BattleUnits), "DoPlacementAIUnitsWithinDeploymentzoneNew", 1);   // AI deploys sides nobody commands
            AddCommander(typeof(BattleUnits), "IsUnitUnderCommander", 1);                        // "does the player command this unit?"
            Add(typeof(BattleUnits), "SetWaypoint", 1);                                       // AI cheat: no order delay near the enemy

            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(HumanPatches), nameof(Transpiler)));
            foreach (var m in Targets.Keys.ToList())
            {
                try { harmony.Patch(m, transpiler: transpiler); }
                catch (Exception e) { Report.Add($"FAILED {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
            }
            foreach (var line in Report) Plugin.Log.LogInfo("human-rules: " + line);
        }

        private static void Add(Type t, string name, params int[] occurrences) => Add(t, name, Rule.Human, occurrences);

        private static void AddCommander(Type t, string name, params int[] occurrences) => Add(t, name, Rule.Commander, occurrences);

        private static void Add(Type t, string name, Rule rule, int[] occurrences)
        {
            // overloads: the one that actually reads playeralliance (the others just forward to it)
            var m = t.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly)
                .Where(x => x.Name == name && !x.IsAbstract && !x.ContainsGenericParameters)
                .OrderByDescending(ReadsPlayerAlliance)
                .FirstOrDefault();
            if (m == null) { Report.Add($"MISSING {t.Name}.{name}"); return; }
            if (occurrences.Length == 0)
            {
                Targets[m] = null;
                AllRule[m] = rule;
                return;
            }
            if (!Targets.TryGetValue(m, out var map) || map == null) Targets[m] = map = new Dictionary<int, Rule>();
            foreach (int o in occurrences) map[o] = rule;
        }

        private static bool ReadsPlayerAlliance(MethodBase m)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(m).Any(p => p.Key == OpCodes.Ldsfld && Equals(p.Value, PlayerAlliance));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCompare(CodeInstruction c) =>
            c != null && (c.opcode == OpCodes.Ceq || c.opcode == OpCodes.Beq || c.opcode == OpCodes.Beq_S
                          || c.opcode == OpCodes.Bne_Un || c.opcode == OpCodes.Bne_Un_S);

        private static bool IsPureLoad(CodeInstruction c) =>
            c != null && (c.IsLdarg() || c.IsLdloc() || c.opcode == OpCodes.Ldc_I4 || c.opcode == OpCodes.Ldc_I4_S
                          || (c.opcode.Value >= OpCodes.Ldc_I4_M1.Value && c.opcode.Value <= OpCodes.Ldc_I4_8.Value)
                          || (c.opcode == OpCodes.Ldsfld && c.operand is FieldInfo f && f.FieldType == typeof(int) && f != PlayerAlliance));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var list = instructions.ToList();
            Targets.TryGetValue(__originalMethod, out var only);
            AllRule.TryGetValue(__originalMethod, out var allRule);
            int seen = 0, rewritten = 0, skipped = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i].opcode == OpCodes.Ldsfld && Equals(list[i].operand, PlayerAlliance))) continue;
                seen++;
                Rule rule = allRule;
                if (only != null && !only.TryGetValue(seen, out rule)) continue;
                var match = rule == Rule.Commander ? MatchCommander : MatchHuman;
                var next = i + 1 < list.Count ? list[i + 1] : null;

                if (IsCompare(next))
                {
                    // <x> ldsfld playeralliance cmp  ->  <x> dup call MatchHuman cmp
                    var dup = new CodeInstruction(OpCodes.Dup) { labels = list[i].labels, blocks = list[i].blocks };
                    list[i] = dup;
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Call, match));
                    rewritten++;
                    continue;
                }
                if (IsPureLoad(next) && IsCompare(i + 2 < list.Count ? list[i + 2] : null))
                {
                    // ldsfld playeralliance <x> cmp  ->  <x> call MatchHuman <x> cmp
                    var first = new CodeInstruction(next.opcode, next.operand) { labels = list[i].labels, blocks = list[i].blocks };
                    list[i] = first;
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Call, match));
                    rewritten++;
                    continue;
                }
                if (next != null && (next.IsLdarg() || next.IsLdloc()) && i + 3 < list.Count
                    && list[i + 2].opcode == OpCodes.Ldfld && list[i + 2].operand is FieldInfo lf && lf.FieldType == typeof(int)
                    && IsCompare(list[i + 3]))
                {
                    // ldsfld playeralliance <obj> ldfld f cmp  ->  <obj> ldfld f call MatchHuman <obj> ldfld f cmp
                    var a = new CodeInstruction(next.opcode, next.operand) { labels = list[i].labels, blocks = list[i].blocks };
                    var b = new CodeInstruction(OpCodes.Ldfld, lf);
                    list[i] = a;
                    list.Insert(i + 1, b);
                    list.Insert(i + 2, new CodeInstruction(OpCodes.Call, match));
                    rewritten++;
                    continue;
                }
                skipped++;
            }
            if (rewritten > 0 || skipped > 0)
                Report.Add($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}: rewrote {rewritten}, left {skipped} (of {seen})");
            return list;
        }
    }

    /// <summary>
    /// The host runs the auto-manage routines for the guest's alliance too; while it does, the guest's own
    /// auto-manage choices replace the host's. Outside a session nothing changes.
    /// </summary>
    [HarmonyPatch]
    internal static class RemotePrefsSwap
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AICampaign), nameof(AICampaign.UpdateCompanyFoundations));
            yield return AccessTools.Method(typeof(AICampaign), nameof(AICampaign.UpdateFinancialAI));
            yield return AccessTools.Method(typeof(AICampaign), nameof(AICampaign.UpdateRailroadConstruction));
            yield return AccessTools.Method(typeof(AICampaign), nameof(AICampaign.CheckWeaponUpgrades));
            yield return AccessTools.Method(typeof(AICampaign), nameof(AICampaign.CheckPurchaseWeapons));
            yield return AccessTools.Method(typeof(AICampaign), nameof(AICampaign.UpdateProjects));
            yield return AccessTools.Method(typeof(Policies), nameof(Policies.CheckAIPolicyChange));
        }

        private static bool Prefix(object[] __args, out AutoPrefs __state)
        {
            __state = null;
            if (!Mp.CampaignRules) return true;
            // the guest never makes AI decisions; the host's results reach it through sync
            if (Mp.Role == Role.Guest) return false;
            if (__args.Length > 0 && __args[0] is int alliance && alliance == Mp.RemoteAlliance)
            {
                __state = AutoPrefs.FromGame();
                Mp.RemotePrefs.ApplyToGame();
            }
            return true;
        }

        private static void Finalizer(AutoPrefs __state)
        {
            __state?.ApplyToGame();
        }
    }
}
