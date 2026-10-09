using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// Which units exist and how they are organised.
    /// Host: once a second compares the unit list with what it last broadcast and sends creations (full record),
    /// deletions and re-parenting. The recruitment panel's preview unit is skipped until it is actually recruited.
    /// Guest: recreates units with the game's own CreateUnitDefault (commander choice forced to the host's), then
    /// copies the host's identity and numbers onto them. The guest's own recruitment/OOB actions are sent to the host
    /// as requests instead of creating units locally.
    /// </summary>
    public static class StructureSync
    {
        private class Known
        {
            public int Parent;
            public bool Active;
        }

        private static readonly Dictionary<int, Known> KnownUnits = new Dictionary<int, Known>();
        private static readonly Dictionary<Regiment, int> PendingTokens = new Dictionary<Regiment, int>();
        private static float _next;
        private static int _token;
        public static int Created, Deleted, Reparented, Requests;

        // guest replay state, read by the patches below
        public static bool Replaying;
        public static int ReplayCommander = -1;

        public static void Reset()
        {
            KnownUnits.Clear();
            PendingTokens.Clear();
            Created = Deleted = Reparented = Requests = 0;
            foreach (var r in GameApi.Units())
                KnownUnits[r.unitid] = new Known { Parent = ParentId(r), Active = r.gameObject.activeSelf };
        }

        public static int ParentId(Regiment r)
        {
            var p = r.transform.parent;
            if (p == null) return 0;
            var pr = p.GetComponent<Regiment>();
            return pr != null ? pr.unitid : 0;
        }

        private static int Depth(Regiment r)
        {
            int d = 0;
            for (var t = r.transform.parent; t != null; t = t.parent)
                if (t.GetComponent<Regiment>() != null) d++;
            return d;
        }

        // ---- host -------------------------------------------------------------------------------------

        public static void HostTick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            var preview = CampaignMainPanel.dummyregiment;
            var current = new Dictionary<int, Regiment>();
            foreach (var r in GameApi.Units())
            {
                if (r == preview || r.unittyp == 5) continue; // preview unit, couriers
                current[r.unitid] = r;
            }

            var created = current.Where(kv => !KnownUnits.ContainsKey(kv.Key)).Select(kv => kv.Value).OrderBy(Depth).ToList();
            var deleted = KnownUnits.Keys.Where(id => !current.ContainsKey(id)).ToList();
            var moved = new List<Regiment>();
            foreach (var kv in current)
            {
                if (!KnownUnits.TryGetValue(kv.Key, out var k)) continue;
                int parent = ParentId(kv.Value);
                bool active = kv.Value.gameObject.activeSelf;
                if (k.Parent != parent || k.Active != active) moved.Add(kv.Value);
            }
            if (created.Count == 0 && deleted.Count == 0 && moved.Count == 0) return;

            // new units may bring new commanders; make sure the guest has them first
            CommanderSync.HostTick(force: true);

            if (created.Count > 0)
            {
                Session.Send(MsgType.UnitCreate, Wire.Build(w =>
                {
                    w.Write(created.Count);
                    foreach (var r in created)
                    {
                        PendingTokens.TryGetValue(r, out int token);
                        PendingTokens.Remove(r);
                        UnitRecord.Capture(r, token).Write(w);
                    }
                }));
                foreach (var r in created) KnownUnits[r.unitid] = new Known { Parent = ParentId(r), Active = r.gameObject.activeSelf };
                Created += created.Count;
            }
            if (moved.Count > 0)
            {
                Session.Send(MsgType.UnitReparent, Wire.Build(w =>
                {
                    w.Write(moved.Count);
                    foreach (var r in moved)
                    {
                        w.Write(r.unitid);
                        w.Write(ParentId(r));
                        w.Write(r.gameObject.activeSelf);
                        w.Write(r.istopunit);
                        w.Write(r.permanentlydetached);
                    }
                }));
                foreach (var r in moved) KnownUnits[r.unitid] = new Known { Parent = ParentId(r), Active = r.gameObject.activeSelf };
                Reparented += moved.Count;
            }
            if (deleted.Count > 0)
            {
                Session.Send(MsgType.UnitDelete, Wire.Build(w =>
                {
                    w.Write(deleted.Count);
                    foreach (var id in deleted) w.Write(id);
                }));
                foreach (var id in deleted) KnownUnits.Remove(id);
                Deleted += deleted.Count;
            }
        }

        private static void Track(Regiment r, int token)
        {
            if (r != null && token != 0) PendingTokens[r] = token;
        }

        /// <summary>Host: a structural action the guest performed on its own alliance.</summary>
        public static void OnRequest(BinaryReader rd)
        {
            var kind = (StructureRequest)rd.ReadByte();
            int token = rd.ReadInt32();
            var bu = GameApi.BU;
            Requests++;
            switch (kind)
            {
                case StructureRequest.Recruit:
                {
                    var parent = GameApi.FindUnit(rd.ReadInt32());
                    int state = rd.ReadInt32(), typ = rd.ReadInt32();
                    bool horse = rd.ReadBoolean();
                    var coat = ReadColor(rd);
                    var trousers = ReadColor(rd);
                    int uniformset = rd.ReadInt32(), recruitingtype = rd.ReadInt32(), size = rd.ReadInt32();
                    if (parent != null && parent.alliance != Mp.GuestAlliance) return;
                    var r = bu.CreateUnitDefault(parent, state, typ, horse, coat, trousers, uniformset, recruitingtype, Mp.GuestAlliance, default, size);
                    if (r == null) { Session.SendNotice("Recruitment could not be carried out on the host."); return; }
                    BattleUnits.RecruitUnit(r);
                    Track(r, token);
                    break;
                }
                case StructureRequest.NewGroup:
                {
                    var parent = GameApi.FindUnit(rd.ReadInt32());
                    if (parent == null || parent.alliance != Mp.GuestAlliance || parent.unittyp - 1 <= 13) return;
                    Track(bu.CreateUnitDefault(parent, -1, parent.unittyp - 1), token);
                    break;
                }
                case StructureRequest.NewTopGroup:
                {
                    int typ = rd.ReadInt32(), state = rd.ReadInt32();
                    var pos = rd.ReadVector3();
                    int moveUnit = rd.ReadInt32();
                    var r = bu.CreateUnitDefault(null, state, typ, false, default, default, 0, 0, Mp.GuestAlliance, pos, -1, false, 16);
                    if (r == null) return;
                    r.unittyp = typ;
                    r.overridesymbol = typ;
                    Track(r, token);
                    var m = GameApi.FindUnit(moveUnit);
                    if (m != null && m.alliance == Mp.GuestAlliance) bu.MoveUnit(m.gameObject, r.gameObject);
                    break;
                }
                case StructureRequest.Batch:
                {
                    var parent = GameApi.FindUnit(rd.ReadInt32());
                    int n = rd.ReadInt32();
                    var counts = new int[n];
                    for (int i = 0; i < n; i++) counts[i] = rd.ReadInt32();
                    int m = rd.ReadInt32();
                    var sizes = new int[m];
                    for (int i = 0; i < m; i++) sizes[i] = rd.ReadInt32();
                    if (parent == null || parent.alliance != Mp.GuestAlliance) return;
                    bu.CreateUnitBatchOperation(parent, counts, sizes, Mp.GuestAlliance);
                    break;
                }
                case StructureRequest.Move:
                {
                    var u = GameApi.FindUnit(rd.ReadInt32());
                    var below = GameApi.FindUnit(rd.ReadInt32());
                    if (u == null || below == null || u.alliance != Mp.GuestAlliance || below.alliance != Mp.GuestAlliance) return;
                    bu.MoveUnit(u.gameObject, below.gameObject, skipdlccheck: true);
                    break;
                }
                case StructureRequest.Detach:
                {
                    var u = GameApi.FindUnit(rd.ReadInt32());
                    if (u == null || u.alliance != Mp.GuestAlliance) return;
                    bu.OrderDetachingFromGroup(u);
                    break;
                }
                case StructureRequest.NoParent:
                {
                    var u = GameApi.FindUnit(rd.ReadInt32());
                    if (u == null || u.alliance != Mp.GuestAlliance) return;
                    bu.SetGroupToNoParent(u);
                    break;
                }
            }
        }

        // ---- guest ------------------------------------------------------------------------------------

        public static void SendRequest(StructureRequest kind, Action<BinaryWriter> body)
        {
            int token = ++_token;
            Session.Send(MsgType.StructureRequest, Wire.Build(w =>
            {
                w.Write((byte)kind);
                w.Write(token);
                body(w);
            }));
            Requests++;
        }

        public static void OnCreate(BinaryReader rd)
        {
            int n = rd.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var rec = UnitRecord.Read(rd);
                try { rec.Recreate(); Created++; }
                catch (Exception e) { Plugin.Log.LogError($"recreating {rec.Name} ({rec.Id}): {e}"); }
            }
            BattleUnits.UpdateUnitList();
        }

        public static void OnDelete(BinaryReader rd)
        {
            int n = rd.ReadInt32();
            Mp.Applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var r = GameApi.FindUnit(rd.ReadInt32());
                    if (r == null) continue;
                    BattleUnits.DeleteUnit(r.gameObject, transferrecruitstopool: false);
                    Deleted++;
                }
            }
            finally { Mp.Applying = false; }
            BattleUnits.UpdateUnitList();
        }

        public static void OnReparent(BinaryReader rd)
        {
            int n = rd.ReadInt32();
            Mp.Applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    int id = rd.ReadInt32(), parentId = rd.ReadInt32();
                    bool active = rd.ReadBoolean(), top = rd.ReadBoolean(), detached = rd.ReadBoolean();
                    var r = GameApi.FindUnit(id);
                    if (r == null) continue;
                    var parent = parentId != 0 ? GameApi.FindUnit(parentId) : null;
                    Reparent(r, parent);
                    r.istopunit = top;
                    r.permanentlydetached = detached;
                    if (r.gameObject.activeSelf != active) r.gameObject.SetActive(active);
                    Reparented++;
                }
            }
            finally { Mp.Applying = false; }
        }

        /// <summary>The attach part of BattleUnits.MoveUnit, without its rules (the host already applied them).</summary>
        public static void Reparent(Regiment r, Regiment parent)
        {
            var oldTop = GameApi.BU.GetHighestParentUnit(r.gameObject, 16);
            var newParentTf = parent != null ? parent.transform : BattleUnits.unitfolder.transform;
            if (r.transform.parent == newParentTf) return;
            r.transform.parent = newParentTf;
            parent?.UpdateAllAttachedUnits();
            if (oldTop != null && oldTop != r) oldTop.UpdateAllAttachedUnits(null, includesubordinategroups: true);
            var newTop = GameApi.BU.GetHighestParentUnit(r.gameObject, 16);
            if (newTop != null && newTop != r) newTop.UpdateAllAttachedUnits(null, includesubordinategroups: true);
            BattleUnits.UpdateAllParentUnits(r.gameObject);
        }

        public static Color ReadColor(BinaryReader r) => new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static void WriteColor(BinaryWriter w, Color c)
        {
            w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
        }
    }

    public enum StructureRequest : byte { Recruit = 1, NewGroup = 2, NewTopGroup = 3, Batch = 4, Move = 5, Detach = 6, NoParent = 7 }

    /// <summary>Everything needed to rebuild one unit on the guest.</summary>
    public class UnitRecord
    {
        public int Id, Rank, Parent, Token;
        public int Typ, OverrideSymbol, Alliance, State, Commander, Weapon, UniformSet, RecruitingType, ContractLength, Battles, BattleFlag, BrigadeRank;
        public bool Horse, FirstBattle, Garrison, GarrisonBasic, Active, Top, Detached, FinishedRecruitment;
        public string Name, Abrv, ServiceHistory, FortName;
        public int Strength, Sick, Wounded, Captured;
        public float Experience, Training, Morale, Intel, TransferTime, LastTransferUpdate, Rotation;
        public Vector3 Position, TransferPosition, FortPosition;
        public Color Coat, Trousers, Variation;
        public int CDay, CMonth, CYear;
        public List<Regiment.Perk> Perks = new List<Regiment.Perk>();

        public static UnitRecord Capture(Regiment r, int token)
        {
            var u = new UnitRecord
            {
                Id = r.unitid, Rank = r.rankofunitintotal, Parent = StructureSync.ParentId(r), Token = token,
                Typ = r.unittyp, OverrideSymbol = r.overridesymbol, Alliance = r.alliance, State = r.state, Commander = r.commander,
                Weapon = r.weapon, UniformSet = r.uniformset, RecruitingType = r.recruitingtype, ContractLength = r.contractlength,
                Battles = r.numberofbattles, BattleFlag = r.battleflag, BrigadeRank = r.rankofbrigadecampaign,
                Horse = r.ishorseartillery, FirstBattle = r.firstbattle, Garrison = r.garrisonreference != null, GarrisonBasic = r.isgarrisonbasicunit,
                Active = r.gameObject.activeSelf, Top = r.istopunit, Detached = r.permanentlydetached, FinishedRecruitment = r.finishedrecruitment,
                Name = r.gameObject.name, Abrv = r.abrvname ?? "", ServiceHistory = r.servicehistory ?? "",
                FortName = r.garrisonreference != null ? r.garrisonreference.gameObject.name : "",
                FortPosition = r.garrisonreference != null ? r.garrisonreference.transform.position : default,
                Strength = r.strength, Sick = r.sick, Wounded = r.losses != null && r.losses.Length > 0 ? r.losses[0] : 0,
                Captured = r.losses != null && r.losses.Length > 3 ? r.losses[3] : 0,
                Experience = r.experience, Training = r.training, Morale = r.morale, Intel = r.intel,
                TransferTime = r.transfertime, LastTransferUpdate = r.lasttransferupdate, TransferPosition = r.currenttransferposition,
                Position = r.transform.position, Rotation = r.transform.rotation.eulerAngles.y,
                Coat = r.componentcolor != null && r.componentcolor.Length > 1 ? r.componentcolor[1] : default,
                Trousers = r.componentcolor != null && r.componentcolor.Length > 0 ? r.componentcolor[0] : default,
                Variation = r.componentcolor != null && r.componentcolor.Length > 3 ? r.componentcolor[3] : default,
            };
            if (r.contractdate != null) { u.CDay = r.contractdate.day; u.CMonth = r.contractdate.month; u.CYear = r.contractdate.year; }
            if (r.perks != null) u.Perks = r.perks;
            return u;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Id); w.Write(Rank); w.Write(Parent); w.Write(Token);
            w.Write(Typ); w.Write(OverrideSymbol); w.Write(Alliance); w.Write(State); w.Write(Commander); w.Write(Weapon);
            w.Write(UniformSet); w.Write(RecruitingType); w.Write(ContractLength); w.Write(Battles); w.Write(BattleFlag); w.Write(BrigadeRank);
            w.Write(Horse); w.Write(FirstBattle); w.Write(Garrison); w.Write(GarrisonBasic); w.Write(Active); w.Write(Top); w.Write(Detached); w.Write(FinishedRecruitment);
            w.WriteStr(Name); w.WriteStr(Abrv); w.WriteStr(ServiceHistory); w.WriteStr(FortName); w.Write(FortPosition);
            w.Write(Strength); w.Write(Sick); w.Write(Wounded); w.Write(Captured);
            w.Write(Experience); w.Write(Training); w.Write(Morale); w.Write(Intel);
            w.Write(TransferTime); w.Write(LastTransferUpdate); w.Write(TransferPosition);
            w.Write(Position); w.Write(Rotation);
            StructureSync.WriteColor(w, Coat); StructureSync.WriteColor(w, Trousers); StructureSync.WriteColor(w, Variation);
            w.Write(CDay); w.Write(CMonth); w.Write(CYear);
            w.Write((byte)Perks.Count);
            foreach (var p in Perks) { w.Write(p.perk); w.Write(p.perklevel); w.Write(p.perkexp); }
        }

        public static UnitRecord Read(BinaryReader r)
        {
            var u = new UnitRecord
            {
                Id = r.ReadInt32(), Rank = r.ReadInt32(), Parent = r.ReadInt32(), Token = r.ReadInt32(),
                Typ = r.ReadInt32(), OverrideSymbol = r.ReadInt32(), Alliance = r.ReadInt32(), State = r.ReadInt32(), Commander = r.ReadInt32(), Weapon = r.ReadInt32(),
                UniformSet = r.ReadInt32(), RecruitingType = r.ReadInt32(), ContractLength = r.ReadInt32(), Battles = r.ReadInt32(), BattleFlag = r.ReadInt32(), BrigadeRank = r.ReadInt32(),
                Horse = r.ReadBoolean(), FirstBattle = r.ReadBoolean(), Garrison = r.ReadBoolean(), GarrisonBasic = r.ReadBoolean(), Active = r.ReadBoolean(), Top = r.ReadBoolean(), Detached = r.ReadBoolean(), FinishedRecruitment = r.ReadBoolean(),
                Name = r.ReadString(), Abrv = r.ReadString(), ServiceHistory = r.ReadString(), FortName = r.ReadString(), FortPosition = r.ReadVector3(),
                Strength = r.ReadInt32(), Sick = r.ReadInt32(), Wounded = r.ReadInt32(), Captured = r.ReadInt32(),
                Experience = r.ReadSingle(), Training = r.ReadSingle(), Morale = r.ReadSingle(), Intel = r.ReadSingle(),
                TransferTime = r.ReadSingle(), LastTransferUpdate = r.ReadSingle(), TransferPosition = r.ReadVector3(),
                Position = r.ReadVector3(), Rotation = r.ReadSingle(),
            };
            u.Coat = StructureSync.ReadColor(r); u.Trousers = StructureSync.ReadColor(r); u.Variation = StructureSync.ReadColor(r);
            u.CDay = r.ReadInt32(); u.CMonth = r.ReadInt32(); u.CYear = r.ReadInt32();
            int pc = r.ReadByte();
            u.Perks = new List<Regiment.Perk>();
            for (int i = 0; i < pc; i++) u.Perks.Add(new Regiment.Perk { perk = r.ReadInt32(), perklevel = r.ReadInt32(), perkexp = r.ReadSingle() });
            return u;
        }

        private static string TagFor(int typ)
        {
            if (typ < 10 || typ == 13) return "Brigade";
            switch (typ)
            {
                case 10: return "Company";
                case 11: return "Battalion";
                case 12: return "Regiment";
                case 14: return "Division";
                case 15: return "Corps";
                case 16: return "Army";
                default: return null;
            }
        }

        /// <summary>Guest: build this unit with the game's own creation code, then copy the host's identity and numbers.</summary>
        public void Recreate()
        {
            if (GameApi.FindUnit(Id) != null) return;
            var parent = Parent != 0 ? GameApi.FindUnit(Parent) : null;
            int state = State;
            if (Typ <= 13 && state < 0) state = 0;
            Regiment r;
            Mp.Applying = true;
            StructureSync.Replaying = true;
            StructureSync.ReplayCommander = Commander;
            try
            {
                r = GameApi.BU.CreateUnitDefault(parent, state, Typ, Horse, Coat, Trousers, UniformSet, RecruitingType, Alliance, Position,
                    Typ <= 13 ? Strength + Sick : -1, Garrison);
            }
            finally
            {
                StructureSync.Replaying = false;
                StructureSync.ReplayCommander = -1;
                Mp.Applying = false;
            }
            if (r == null) throw new InvalidOperationException("CreateUnitDefault returned null");

            r.gameObject.name = Name;
            r.abrvname = Abrv;
            r.unittyp = Typ;
            r.overridesymbol = OverrideSymbol;
            var tag = TagFor(Typ);
            if (tag != null) r.gameObject.tag = tag;
            r.rankofunitintotal = Rank;
            r.unitid = Id;
            r.state = State;
            r.strength = Strength;
            r.sick = Sick;
            if (r.losses != null && r.losses.Length > 3) { r.losses[0] = Wounded; r.losses[3] = Captured; }
            r.experience = Experience;
            r.training = Training;
            r.morale = Morale;
            r.intel = Intel;
            r.weapon = Weapon;
            r.firstbattle = FirstBattle;
            r.numberofbattles = Battles;
            r.battleflag = BattleFlag;
            r.rankofbrigadecampaign = BrigadeRank;
            r.contractlength = ContractLength;
            r.contractdate = new Tools.Date(CDay, CMonth, CYear);
            r.servicehistory = ServiceHistory;
            r.uniformset = UniformSet;
            r.isgarrisonbasicunit = GarrisonBasic;
            r.finishedrecruitment = FinishedRecruitment;
            r.transfertime = TransferTime;
            r.lasttransferupdate = LastTransferUpdate;
            r.currenttransferposition = TransferPosition;
            r.istopunit = Top;
            r.permanentlydetached = Detached;
            if (Perks.Count > 0) r.perks = Perks;
            if (Garrison && FortName.Length > 0)
            {
                var fort = BattleUnits.fort?.FirstOrDefault(f => f != null && f.gameObject.name == FortName && Vector3.Distance(f.transform.position, FortPosition) < 2f);
                if (fort != null)
                {
                    r.garrisonreference = fort;
                    if (Typ > 13) fort.garrisonunit = r;
                }
            }
            if (r.gameObject.activeSelf != Active) r.gameObject.SetActive(Active);
            if (Token != 0) Plugin.Log.LogInfo($"recreated requested unit {Name} (token {Token})");
            r.UpdateGroupStrength(overridecalc: true, ignoreactivestate: true);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Patches that make the replay deterministic and route the guest's own actions to the host.
    // ---------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(GameVars), nameof(GameVars.GetSuitableCommander))]
    internal static class ReplayCommanderChoice
    {
        private static bool Prefix(ref int __result)
        {
            if (!StructureSync.Replaying) return true;
            __result = StructureSync.ReplayCommander;
            return false;
        }
    }

    /// <summary>create_regiment would invent a placeholder commander for garrisons; use the host's instead.</summary>
    [HarmonyPatch(typeof(BattleUnits), "create_regiment")]
    internal static class ReplayCommanderArgument
    {
        private static void Prefix(ref int commander)
        {
            if (StructureSync.Replaying && StructureSync.ReplayCommander >= 0 && StructureSync.ReplayCommander < GameVars.commander.Count)
                commander = StructureSync.ReplayCommander;
        }
    }

    /// <summary>Garrisons on the guest arrive from the host (with their fort link) instead of being made locally.</summary>
    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.CreateGarrisonDummy))]
    internal static class GuestNoGarrisonCreation
    {
        private static bool Prefix() => !(Mp.IsGuest && Mp.CampaignRules && !Mp.Applying);
    }

    internal static class GuestRoute
    {
        public static bool Active => Mp.IsGuest && Session.IsLive && GameApi.InCampaignScene && !Mp.Applying;
    }

    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.RecruitUnit))]
    internal static class GuestRecruit
    {
        private static bool Prefix(Regiment dummyregiment)
        {
            if (!GuestRoute.Active || dummyregiment == null || dummyregiment.alliance != Mp.GuestAlliance) return true;
            var d = dummyregiment;
            int parent = StructureSync.ParentId(d);
            StructureSync.SendRequest(StructureRequest.Recruit, w =>
            {
                w.Write(parent);
                w.Write(d.state);
                w.Write(d.unittyp);
                w.Write(d.ishorseartillery);
                StructureSync.WriteColor(w, d.componentcolor != null && d.componentcolor.Length > 1 ? d.componentcolor[1] : default);
                StructureSync.WriteColor(w, d.componentcolor != null && d.componentcolor.Length > 0 ? d.componentcolor[0] : default);
                w.Write(d.uniformset);
                w.Write(d.recruitingtype);
                w.Write(d.strength + d.sick);
            });
            // the preview unit only exists here; the real one arrives from the host
            BattleUnits.DeleteUnit(d.gameObject, transferrecruitstopool: false);
            Session.Notice("Recruitment sent to the host.");
            return false;
        }
    }

    [HarmonyPatch(typeof(CampaignMainPanel), nameof(CampaignMainPanel.RecruitGroup))]
    internal static class GuestNewGroup
    {
        private static bool Prefix(Regiment _unittorecruitfor)
        {
            if (!GuestRoute.Active || _unittorecruitfor == null) return true;
            StructureSync.SendRequest(StructureRequest.NewGroup, w => w.Write(_unittorecruitfor.unitid));
            return false;
        }
    }

    [HarmonyPatch(typeof(CampaignMainPanel), "CreateCampaignGroup")]
    internal static class GuestNewTopGroup
    {
        private static bool Prefix(int unittyp, int state, Vector3 forceposition, ref Regiment __result)
        {
            if (!GuestRoute.Active) return true;
            StructureSync.SendRequest(StructureRequest.NewTopGroup, w => { w.Write(unittyp); w.Write(state); w.Write(forceposition); w.Write(0); });
            __result = null;
            return false;
        }
    }

    /// <summary>Detach to an independent corps = new corps + move; do both on the host in one request.</summary>
    [HarmonyPatch(typeof(CampaignMainPanel), nameof(CampaignMainPanel.DetachUnitToIndependentCorps))]
    internal static class GuestDetachToCorps
    {
        private static bool Prefix(CampaignMainPanel __instance)
        {
            if (!GuestRoute.Active) return true;
            var u = Traverse.Create(__instance).Field("unittosplit").GetValue<Regiment>();
            if (u == null || u.unittyp >= 16 || u.garrisonreference != null) return false;
            if (u.unittyp == 15)
            {
                StructureSync.SendRequest(StructureRequest.NoParent, w => w.Write(u.unitid));
                return false;
            }
            var top = GameApi.BU.GetHighestParentUnit(u.gameObject, 15);
            var pos = top != null ? top.transform.position : u.transform.position;
            StructureSync.SendRequest(StructureRequest.NewTopGroup, w => { w.Write(15); w.Write(u.state); w.Write(pos); w.Write(u.unitid); });
            return false;
        }
    }

    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.CreateUnitBatchOperation))]
    internal static class GuestBatchRecruit
    {
        private static bool Prefix(Regiment grouptoadd, int[] numberofunittypes, int[] unitsizes)
        {
            if (!GuestRoute.Active || grouptoadd == null || grouptoadd.alliance != Mp.GuestAlliance) return true;
            StructureSync.SendRequest(StructureRequest.Batch, w =>
            {
                w.Write(grouptoadd.unitid);
                w.Write(numberofunittypes.Length);
                foreach (var n in numberofunittypes) w.Write(n);
                w.Write(unitsizes.Length);
                foreach (var s in unitsizes) w.Write(s);
            });
            return false;
        }
    }

    /// <summary>OOB drag and drop: done locally for immediate feedback and on the host for real.</summary>
    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.MoveUnit))]
    internal static class GuestOobMove
    {
        private static void Prefix(GameObject unittomove, GameObject unittomovebelow)
        {
            if (!GuestRoute.Active || unittomove == null || unittomovebelow == null) return;
            var a = unittomove.GetComponent<Regiment>();
            var b = unittomovebelow.GetComponent<Regiment>();
            if (a == null || b == null || a.alliance != Mp.GuestAlliance) return;
            StructureSync.SendRequest(StructureRequest.Move, w => { w.Write(a.unitid); w.Write(b.unitid); });
        }
    }

    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.OrderDetachingFromGroup))]
    internal static class GuestDetach
    {
        private static void Prefix(Regiment reg)
        {
            if (!GuestRoute.Active || reg == null || reg.alliance != Mp.GuestAlliance) return;
            StructureSync.SendRequest(StructureRequest.Detach, w => w.Write(reg.unitid));
        }
    }

    [HarmonyPatch(typeof(BattleUnits), nameof(BattleUnits.SetGroupToNoParent))]
    internal static class GuestNoParent
    {
        private static void Prefix(Regiment __0)
        {
            if (!GuestRoute.Active || __0 == null || __0.alliance != Mp.GuestAlliance) return;
            StructureSync.SendRequest(StructureRequest.NoParent, w => w.Write(__0.unitid));
        }
    }
}
