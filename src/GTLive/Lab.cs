using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GTLive
{
    /// <summary>
    /// Test harness: with --gtlive-lab the plugin polls C:\gtlive\lab\&lt;id&gt;\cmd.txt and appends results to out.txt.
    /// Lets the two test instances be driven from a script without clicking through the game's UI.
    /// </summary>
    public static class Lab
    {
        public static string Dir;
        private static float _nextPoll;
        private static Stopwatch _loadTimer;
        private static bool _wasReady;

        public static void Tick()
        {
            if (!Plugin.LabMode) return;
            if (Dir == null)
            {
                Dir = Path.Combine(@"C:\gtlive\lab", string.IsNullOrEmpty(Plugin.InstanceId) ? "main" : Plugin.InstanceId);
                Directory.CreateDirectory(Dir);
                Out("lab started, frame " + GameVars.frame);
            }

            bool ready = GameApi.CampaignReady;
            if (ready && !_wasReady && _loadTimer != null)
            {
                Out($"campaign ready after {_loadTimer.Elapsed.TotalSeconds:F1}s, alliance {GameVars.playeralliance}, date {GameApi.CampaignDateString()}");
                _loadTimer = null;
            }
            _wasReady = ready;

            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.25f;
            string cmdFile = Path.Combine(Dir, "cmd.txt");
            if (!File.Exists(cmdFile)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(cmdFile);
                File.Delete(cmdFile);
            }
            catch (IOException) { return; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { Out("> " + line + "\n" + Execute(line)); }
                catch (Exception e) { Out("> " + line + "\nERROR " + e); }
            }
        }

        public static void Out(string text)
        {
            if (Dir == null) return;
            File.AppendAllText(Path.Combine(Dir, "out.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\n");
        }

        private static string Execute(string line)
        {
            var a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var cmd = a[0].ToLowerInvariant();
            switch (cmd)
            {
                case "ping":
                    return "pong";
                case "status":
                    return Status();
                case "load":
                    // load <level> <sub> <folder> <alliance>
                    _loadTimer = Stopwatch.StartNew();
                    GameApi.LoadCampaignSave(a[1], a[2], a[3], int.Parse(a[4]));
                    return "loading";
                case "save":
                {
                    var sw = Stopwatch.StartNew();
                    var dir = GameApi.SaveCampaignTo(a[1], a.Length > 2 ? a[2] : "lab");
                    long bytes = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                    return $"saved {dir} in {sw.ElapsedMilliseconds} ms, {bytes / 1024} KB";
                }
                case "speed":
                    GameApi.SetSpeed(float.Parse(a[1], CultureInfo.InvariantCulture));
                    return "speed " + GameVars.gamespeed + " paused " + GameVars.gamepaused;
                case "units":
                    return Units(a.Length > 1 ? int.Parse(a[1]) : -1);
                case "unit":
                    return UnitDetail(int.Parse(a[1]));
                case "move":
                {
                    // move <unitid> <x> <z>: the same call the campaign map makes for a right-click order
                    var r = GameApi.FindUnit(int.Parse(a[1]));
                    if (r == null) return "no such unit";
                    float x = float.Parse(a[2], CultureInfo.InvariantCulture), z = float.Parse(a[3], CultureInfo.InvariantCulture);
                    var t = Terrain.activeTerrain;
                    var pos = new Vector3(x, t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y, z);
                    GameApi.BU.SetWaypoint(r, pos, newpath: true, doublequick: false, manualfinalrotation: -1f, modifylastwaypoint: false,
                        useorderdelay: true, timetomove: -1f, direction: -1, showmovementoptions: false);
                    return UnitDetail(r.unitid);
                }
                case "place":
                {
                    // place <unitid> <x> <z>: puts a campaign formation somewhere at once (testing only)
                    var r = GameApi.FindUnit(int.Parse(a[1]));
                    if (r == null) return "no such unit";
                    float x = float.Parse(a[2], CultureInfo.InvariantCulture), z = float.Parse(a[3], CultureInfo.InvariantCulture);
                    var t = Terrain.activeTerrain;
                    var pos = new Vector3(x, t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y, z);
                    var origin = r.GetPosition();
                    foreach (var c in r.GetComponentsInChildren<Regiment>(true))
                    {
                        var off = c.GetPosition() - origin;
                        c.SetPosition(pos + new Vector3(off.x, 0f, off.z));
                    }
                    r.StopRegiment(skipfinalrotation: true, manualstop: true);
                    return UnitDetail(r.unitid);
                }
                case "stance":
                {
                    // stance <unitid> <0 attack | 1 defend>: the campaign stance buttons
                    var r = GameApi.FindUnit(int.Parse(a[1]));
                    if (r == null) return "no such unit";
                    int s = int.Parse(a[2]);
                    r.campaignstanceordered = s;
                    GameApi.BU.PerformActionForGroupForAllUnitsOnLevel(r.gameObject, -1, "SetCampaignStance", s);
                    return UnitDetail(r.unitid);
                }
                case "shot":
                {
                    var file = Path.Combine(Dir, a.Length > 1 ? a[1] : "shot.png");
                    ScreenCapture.CaptureScreenshot(file);
                    return "screenshot -> " + file;
                }
                case "recruit":
                {
                    // recruit <parentId> <typ>: same calls as the recruitment panel (preview unit, then RecruitUnit)
                    var parent = GameApi.FindUnit(int.Parse(a[1]));
                    if (parent == null) return "no such parent";
                    int typ = int.Parse(a[2]);
                    var sample = parent.GetComponentsInChildren<Regiment>(true).FirstOrDefault(x => x.unittyp <= 13 && x.state >= 0);
                    int state = sample != null ? sample.state : 0;
                    int size = BattleUnits.GetMaxUnitSize(parent.alliance, typ, 1, false);
                    var dummy = GameApi.BU.CreateUnitDefault(parent, state, typ, false, default, default, 0, 0, -1, default, size);
                    if (dummy == null) return "CreateUnitDefault returned null";
                    CampaignMainPanel.dummyregiment = dummy;
                    string name = dummy.name;
                    BattleUnits.RecruitUnit(dummy);
                    CampaignMainPanel.dummyregiment = null;
                    return $"recruited {name} (state {state}, size {size}) under {parent.name}";
                }
                case "newgroup":
                {
                    var parent = GameApi.FindUnit(int.Parse(a[1]));
                    var panel = UnityEngine.Object.FindObjectOfType<CampaignMainPanel>(true);
                    panel.RecruitGroup(parent);
                    return "RecruitGroup called under " + parent?.name;
                }
                case "oob":
                    GameApi.BU.MoveUnit(GameApi.FindUnit(int.Parse(a[1])).gameObject, GameApi.FindUnit(int.Parse(a[2])).gameObject);
                    return UnitDetail(int.Parse(a[1])) + " parent=" + Sync.StructureSync.ParentId(GameApi.FindUnit(int.Parse(a[1])));
                case "disband":
                    BattleUnits.DeleteUnit(GameApi.FindUnit(int.Parse(a[1])).gameObject, true);
                    BattleUnits.UpdateUnitList();
                    return "deleted";
                case "children":
                {
                    var p = GameApi.FindUnit(int.Parse(a[1]));
                    if (p == null) return "no such unit";
                    var sb = new StringBuilder();
                    foreach (Transform c in p.transform)
                    {
                        var r = c.GetComponent<Regiment>();
                        if (r != null) sb.AppendLine($"  {r.unitid}\t{r.unittyp}\t{r.name}\tstr={r.strength}\tcmd={r.commander}\tactive={r.gameObject.activeSelf}");
                    }
                    return p.name + ":\n" + sb;
                }
                case "dupes":
                {
                    var groups = GameApi.Units().GroupBy(r => r.unitid).Where(g => g.Count() > 1).ToList();
                    return $"{GameApi.Units().Count()} units, {groups.Count} duplicated ids: " + string.Join("; ", groups.Take(10).Select(g => g.Key + "=" + string.Join("/", g.Select(r => r.name))));
                }
                case "research":
                {
                    var p = Policy.GetPolicyFromID(int.Parse(a[1]));
                    if (p == null) return "no such policy";
                    Policies.AddResearch(GameVars.playeralliance, p);
                    return "researching " + p.name;
                }
                case "canresearch":
                {
                    int al = int.Parse(a[1]);
                    return string.Join(", ", Policy.allpolicies.Where(p => Policy.GetAllianceFromPolicy(p) == al && Policies.CanBeActivated(al, p))
                        .Select(p => p.UniquePolicyID + ":" + p.name));
                }
                case "policies":
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < 2; i++)
                        sb.AppendLine(i + ": " + string.Join(", ", GameVars.alliance[i].activatedpolicies.Select(p => Policy.GetIDFromPolicy(p) + ":" + p.name + "=" + p.researchprogress.ToString("F2"))));
                    return sb.ToString();
                }
                case "build":
                {
                    // build <type> <x> <z>  (supply depot = CBuilding.id_supplydepot)
                    int type = a[1] == "depot" ? CBuilding.id_supplydepot : a[1] == "fort" ? CBuilding.id_fort : int.Parse(a[1]);
                    float x = float.Parse(a[2], CultureInfo.InvariantCulture), z = float.Parse(a[3], CultureInfo.InvariantCulture);
                    var t = Terrain.activeTerrain;
                    var pos = new Vector3(x, t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y, z);
                    var b = CBuilding.Place(pos, type, GameVars.playeralliance, 0, 0f, 0f, newlycreated: true, 0f, pay: true);
                    return b == null ? "Place returned null" : $"placed {b.gameObject.name} type {b.BuildingType} at {pos}";
                }
                case "buildings":
                {
                    var bl = BattleUnits.cbuildings;
                    return $"{bl.Count} buildings; " + string.Join(", ", bl.Where(b => b != null).GroupBy(b => b.Owner + "/" + b.BuildingType).Select(g => g.Key + "x" + g.Count()))
                        + (a.Length > 1 ? "\n" + string.Join("\n", bl.Where(b => b != null && b.gameObject.name.Contains(a[1])).Select(b => $"{b.gameObject.name} owner={b.Owner} lvl={b.level} timer={b.constructiontimer:F2} pos={b.transform.position}")) : "");
                }
                case "weaponorder":
                    WeaponList.PlaceWeaponOrder(GameVars.playeralliance, int.Parse(a[1]), int.Parse(a[2]));
                    return "order placed: " + GameVars.alliance[GameVars.playeralliance].weaponorders[int.Parse(a[1])].quantity;
                case "rails":
                {
                    var rl = BattleUnits.railroad;
                    var buildable = Enumerable.Range(0, rl.Count).Where(i => rl[i].StartConstruction(GameVars.playeralliance, checkonly: true)).Take(10);
                    var building = Enumerable.Range(0, rl.Count).Where(i => rl[i].alliancetoconstruct >= 0 && rl[i].constructionprogress < 1f);
                    return "buildable: " + string.Join(", ", buildable.Select(i => i + ":" + rl[i].scriptref.RailroadName)) +
                           "\nunder construction: " + string.Join(", ", building.Select(i => $"{i}:{rl[i].scriptref.RailroadName} by {rl[i].alliancetoconstruct} {rl[i].constructionprogress:F3}"));
                }
                case "rail":
                    return "started=" + BattleUnits.railroad[int.Parse(a[1])].StartConstruction(GameVars.playeralliance);
                case "battlepanel":
                {
                    var bui = GameApi.BU?.GetComponent<BattleUI>();
                    if (bui == null) return "no BattleUI";
                    if (a.Length > 1) bui.SetBattlePanelShownState(int.Parse(a[1]));
                    return "battlepanelshown=" + bui.battlepanelshown + " op=" + GameApi.Operation;
                }
                case "fight":
                {
                    // take over an ongoing land engagement as a played battle (the autoresolve panel's "fight" button)
                    var bfs = GameApi.BU.GetComponent<BattlefieldSetup>();
                    var m = bfs.battlemonument.FirstOrDefault(x => x != null && x.autocalcref != null && x.landorsea == 0);
                    if (m == null) return "no ongoing land engagement";
                    m.autocalcref.StartManualBattleFromAutocalc();
                    return "launching played battle: " + m.battlename;
                }
                case "bfmarkers":
                {
                    // bfmarkers [map]: campaign battlefield markers (optionally only those using battlefield map <map>)
                    var sb = new StringBuilder();
                    var list = BattleUnits.battlefieldmarkers;
                    float date = GameVars.GetCampaignFloatingDate();
                    for (int i = 0; i < list.Count; i++)
                    {
                        var m = list[i];
                        if (m == null || m.NavalMarker) continue;
                        string refs = m.ScenarioReference + (m.RandomScenarioReferences != null && m.RandomScenarioReferences.Length > 0 ? "|" + string.Join("|", m.RandomScenarioReferences) : "");
                        if (a.Length > 1 && !refs.Contains(a[1])) continue;
                        bool active = date >= Tools.GetFloatingDateFromString(m.DateActiveFrom) && Tools.GetFloatingDateFromString(m.DateActiveTo) > date;
                        sb.AppendLine($"{i}: map={refs} pos=({m.transform.position.x:F1},{m.transform.position.z:F1}) active={active} historical={m.IsHistorical} range={m.HistoricalBfRange} name='{m.OptionalName}' {m.DateActiveFrom}-{m.DateActiveTo}");
                    }
                    return sb.ToString();
                }
                case "finishtwice":
                {
                    // finishtwice: what Autocalc.UpdateSiegeCombat does when its combat cycle already ended the battle
                    var bfs = GameApi.BU.GetComponent<BattlefieldSetup>();
                    var m = bfs.battlemonument.FirstOrDefault(x => x != null && x.autocalcref != null && x.landorsea == 0);
                    if (m == null) return "no ongoing land engagement";
                    var ac = m.autocalcref;
                    var finish = HarmonyLib.AccessTools.Method(typeof(Autocalc), "FinishLandBattle");
                    string name = m.battlename;
                    finish.Invoke(ac, new object[] { 0, 0 });
                    string second;
                    try { finish.Invoke(ac, new object[] { 0, 0 }); second = "second call returned"; }
                    catch (Exception e) { second = "second call threw " + (e.InnerException ?? e).GetType().Name; }
                    return $"{name}: first call finished it (destroyed={ac == null}); {second}; skipped={Patches.FinishLandBattleTwice.Skipped}";
                }
                case "endbattle":
                    GameApi.BU.SetEndOfBattle(11);
                    return "ending battle, op=" + GameApi.Operation;
                case "econ":
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < 2 && i < GameVars.alliance.Length; i++)
                    {
                        var al = GameVars.alliance[i];
                        sb.AppendLine($"{i} {al.name}: treasury={al.treasury:F0} debt={al.debt:F0} morale={al.nationalmorale:F3} tax=[{string.Join(",", al.tax.Select(t => t.ToString("F3", CultureInfo.InvariantCulture)))}] " +
                                      $"subs=[{string.Join(",", al.subsidies.Select(t => t.ToString("F2", CultureInfo.InvariantCulture)))}] policies={al.activatedpolicies.Count} weapons0={al.weaponstock[0]:F0}");
                    }
                    return sb.ToString();
                }
                case "tax":
                {
                    // tax <index> <value>: what moving a Finances tax slider does for the local player's alliance
                    var al = GameVars.alliance[GameVars.playeralliance];
                    al.tax[int.Parse(a[1])] = float.Parse(a[2], CultureInfo.InvariantCulture);
                    return "tax now " + string.Join(",", al.tax.Select(t => t.ToString("F3", CultureInfo.InvariantCulture)));
                }
                case "session":
                    return Session.LabCommand(a.Skip(1).ToArray());
                case "bstate":
                    return BattleStatus();
                case "bunits":
                    return BattleUnitList(a.Length > 1 ? int.Parse(a[1]) : -1, a.Length > 2 ? int.Parse(a[2]) : 40);
                case "bunit":
                    return BattleUnitDetail(int.Parse(a[1]));
                case "bdump":
                {
                    // bdump <file>: every unit's position, facing and strength, for comparing host and guest
                    var sb = new StringBuilder();
                    foreach (var r in Battle.BattleSync.AllUnits().OrderBy(x => Battle.BattleSync.NetId(x)))
                    {
                        if (!r.gameObject.activeInHierarchy) continue;
                        var p = r.GetPosition();
                        sb.AppendLine(string.Join("\t", Battle.BattleSync.NetId(r), r.alliance, r.unittyp, p.x.ToString("F2", CultureInfo.InvariantCulture), p.z.ToString("F2", CultureInfo.InvariantCulture),
                            r.transform.eulerAngles.y.ToString("F1", CultureInfo.InvariantCulture), r.strength, r.formation, r.regimentpaths, r.isrouted ? 1 : 0, r.morale.ToString("F2", CultureInfo.InvariantCulture)));
                    }
                    var file = Path.Combine(Dir, a.Length > 1 ? a[1] : "bdump.txt");
                    File.WriteAllText(file, sb.ToString());
                    return "dumped " + file;
                }
                case "bmove":
                {
                    // bmove <unitid> <x> <z> [rot]: a right-click move order, as the battle UI gives it
                    var r = Battle.BattleSync.Find(int.Parse(a[1]));
                    if (r == null) return "no such unit";
                    float x = float.Parse(a[2], CultureInfo.InvariantCulture), z = float.Parse(a[3], CultureInfo.InvariantCulture);
                    float rot = a.Length > 4 ? float.Parse(a[4], CultureInfo.InvariantCulture) : -1f;
                    var t = Terrain.activeTerrain;
                    var pos = new Vector3(x, t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y, z);
                    AsUi(() => GameApi.BU.SetWaypoint(r, pos, newpath: true, doublequick: false, manualfinalrotation: rot, modifylastwaypoint: false,
                        useorderdelay: true, timetomove: -1f, direction: -1, showmovementoptions: false));
                    return BattleUnitDetail(Battle.BattleSync.NetId(r));
                }
                case "bformation":
                {
                    var r = Battle.BattleSync.Find(int.Parse(a[1]));
                    if (r == null) return "no such unit";
                    AsUi(() => GameApi.BU.SetFormation(r.gameObject, int.Parse(a[2])));
                    return BattleUnitDetail(Battle.BattleSync.NetId(r));
                }
                case "bbehavior":
                {
                    var r = Battle.BattleSync.Find(int.Parse(a[1]));
                    if (r == null) return "no such unit";
                    AsUi(() => GameApi.BU.ChangeCombatBehavior(r.gameObject, int.Parse(a[2])));
                    return BattleUnitDetail(Battle.BattleSync.NetId(r));
                }
                case "btarget":
                {
                    var r = Battle.BattleSync.Find(int.Parse(a[1]));
                    var t = Battle.BattleSync.Find(int.Parse(a[2]));
                    if (r == null || t == null) return "no such unit";
                    AsUi(() => r.AssignTarget(t.gameObject));
                    return BattleUnitDetail(Battle.BattleSync.NetId(r));
                }
                case "barrivals":
                {
                    var bu = GameApi.BU;
                    var sb = new StringBuilder($"{bu.scheduledarrival.Count} scheduled arrivals, {Battle.BattleSync.Arrivals} arrival events\n");
                    foreach (var s in bu.scheduledarrival)
                        sb.AppendLine($"  {Battle.BattleSync.NetId(s.regimentrefreg)} {s.regimentrefreg?.name} left={s.durationleft:F3}h entry={(s.entrypoint != null ? s.entrypoint.name : "-")} queue={s.arrivalqueue.Count} active={s.regimentref.activeInHierarchy}");
                    return sb.ToString();
                }
                case "bpontoons":
                    return Battle.BattleEngineering.PontoonList();
                case "beng":
                    return Battle.BattleEngineering.Describe();
                case "bpontoon":
                {
                    // bpontoon <unitid> <index> [force]: right-click a pontoon site with an engineer unit selected
                    var r = Battle.BattleSync.Find(int.Parse(a[1]));
                    var all = Battle.BattleEngineering.AllPontoons();
                    int index = int.Parse(a[2]);
                    var p = index >= 0 && index < all.Count ? all[index] : null;
                    if (r == null || p == null) return "no such unit or pontoon";
                    if (a.Length > 3 && a[3] == "force") r.hasengineers = true; // testing: hand the unit a pontoon train
                    if (!r.hasengineers) return r.name + " has no engineers";
                    int slot = p.allocatedreg[0] == null ? (p.allocatedreg[1] == null ? (Vector3.Distance(r.transform.position, p.landingzone[0]) < Vector3.Distance(r.transform.position, p.landingzone[1]) ? 0 : 1) : 0) : 1;
                    AsUi(() =>
                    {
                        r.StopRegiment(skipfinalrotation: true, manualstop: true);
                        r.RegimentSetPath(p.landingzone[slot], newpath: true, skipcover: true, p.transform.rotation.eulerAngles.y - 90f);
                        r.pontoonconstruction = p.gameObject;
                        p.allocatedreg[slot] = r.gameObject;
                    });
                    if (Mp.IsGuest) Battle.BattleEngineering.SendGuestPontoon(p);
                    return Battle.BattleEngineering.PontoonList();
                }
                case "bpontoonfinish":
                {
                    // bpontoonfinish <index>: build it at once with deployment engineering points
                    var all = Battle.BattleEngineering.AllPontoons();
                    var p = all[int.Parse(a[1])];
                    AsUi(() => p.FinishImmediately(99999f));
                    if (Mp.IsGuest) Battle.BattleEngineering.SendGuestPontoon(p);
                    return Battle.BattleEngineering.PontoonList();
                }
                case "bobstacle":
                {
                    // bobstacle <0 trench|1 barricade> <x1> <z1> <x2> <z2> [unitid|instant]: draw a line, then build it
                    var bui = GameApi.BUI;
                    int typ = int.Parse(a[1]);
                    var t = Terrain.activeTerrain;
                    Vector3 P(string xs, string zs)
                    {
                        float x = float.Parse(xs, CultureInfo.InvariantCulture), z = float.Parse(zs, CultureInfo.InvariantCulture);
                        return new Vector3(x, t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y, z);
                    }
                    var p1 = P(a[2], a[3]);
                    var p2 = P(a[4], a[5]);
                    string how = a.Length > 6 ? a[6] : "";
                    ObstacleLine oline = null;
                    AsUi(() =>
                    {
                        oline = bui.gameObject.AddComponent<ObstacleLine>();
                        bui.obstaclelines.Add(oline);
                        oline.Initiate(typ);
                        bui.obstaclebuilding = typ;
                        oline.SetNewPoint(p1, fix: true, skipdeploymentzonecheck: true);
                        oline.SetNewPoint(p2, fix: false, skipdeploymentzonecheck: true);
                        oline.SetNewPoint(p2, fix: true, skipdeploymentzonecheck: true);
                        oline.Finalize();
                        bui.obstaclebuilding = -1;
                        if (how == "instant") oline.FinishImmediately(99999f);
                        else if (how.Length > 0)
                        {
                            var u = Battle.BattleSync.Find(int.Parse(how));
                            if (u != null) oline.AddUnitToConstruction(u.gameObject);
                        }
                    });
                    if (Mp.IsGuest) Battle.BattleEngineering.SendNewGuestLines();
                    int placed = oline.modelobjects.Count(m => m.modelobj != null && m.state == 1);
                    return $"line with {placed} segments; " + Battle.BattleEngineering.Describe();
                }
                case "bcease":
                {
                    // bcease: the cease-fire button, then the prompt's left button
                    var bu = GameApi.BU;
                    AsUi(() =>
                    {
                        bu.SetEndOfBattle(10);
                        bu.SetEndOfBattle(-1);
                        bu.triggerendofday = true;
                    });
                    return BattleStatus();
                }
                case "bceaseanswer":
                    Battle.BattleCeaseFire.Answer(a.Length > 1 && a[1] == "yes");
                    return BattleStatus();
                case "bcharge":
                {
                    var r = Battle.BattleSync.Find(int.Parse(a[1]));
                    var t = Battle.BattleSync.Find(int.Parse(a[2]));
                    if (r == null || t == null) return "no such unit";
                    AsUi(() => r.AssignChargeTarget(t.gameObject, usechargeorder: true));
                    return BattleUnitDetail(Battle.BattleSync.NetId(r));
                }
                case "bready":
                {
                    // what the play button does in deployment
                    float sp = a.Length > 1 ? float.Parse(a[1], CultureInfo.InvariantCulture) : 1f;
                    AsUi(() =>
                    {
                        GameVars.SetGameSpeed(sp, overrideminmax: false, manualset: true);
                        GameApi.BUI.SetActiveDeploymentPhase(active: false);
                    });
                    return BattleStatus();
                }
                case "btake":
                    Battle.BattleSync.RequestTakeCommand();
                    return BattleStatus();
                case "barmy":
                {
                    // barmy retreat|surrender: the army orders panel, then the confirmation prompt's left button
                    var bu = GameApi.BU;
                    AsUi(() =>
                    {
                        if (a[1] == "retreat")
                        {
                            bu.SetEndOfBattle(3);
                            if (bu.eodcycle <= 0) GameApi.TP.SetRetreatTimer(GameVars.playeralliance);
                            else { bu.SetEndOfBattle(4); bu.SetEndOfBattle(11); }
                        }
                        else if (a[1] == "surrender")
                        {
                            bu.SetEndOfBattle(5);
                            bu.SetEndOfBattle(11);
                        }
                    });
                    return BattleStatus();
                }
                case "bcontinue":
                {
                    // the Continue button of the open prompt
                    var ph = GameApi.PH;
                    int was = ph.prompt_panelopen;
                    AsUi(() => ph.HidePanel(ph.panel, ph.panel_prompt, markedtodestroy: false, hideimmediately: false, resetbuttonstates: false));
                    return $"closed prompt {was}; " + BattleStatus();
                }
                case "bplace":
                {
                    // bplace <unitid> <x> <z> [rot]: puts a unit somewhere at once (testing only)
                    var r = Battle.BattleSync.Find(int.Parse(a[1]));
                    if (r == null) return "no such unit";
                    float x = float.Parse(a[2], CultureInfo.InvariantCulture), z = float.Parse(a[3], CultureInfo.InvariantCulture);
                    float rot = a.Length > 4 ? float.Parse(a[4], CultureInfo.InvariantCulture) : r.transform.eulerAngles.y;
                    var t = Terrain.activeTerrain;
                    var pos = new Vector3(x, t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y, z);
                    AsUi(() =>
                    {
                        foreach (var c in r.GetComponentsInChildren<Regiment>())
                        {
                            var off = c.GetPosition() - r.GetPosition();
                            c.SetNewPosition(pos + new Vector3(off.x, 0f, off.z), rot, usecoverplacement: false, ignoredeploymentzone: true);
                        }
                    });
                    return BattleUnitDetail(Battle.BattleSync.NetId(r));
                }
                case "quit":
                    Application.Quit();
                    return "quitting";
                default:
                    return "unknown command";
            }
        }

        private static string Status()
        {
            var sb = new StringBuilder();
            sb.Append($"op={GameApi.Operation} frame={GameVars.frame} campaignScene={GameApi.InCampaignScene} ready={GameApi.CampaignReady}");
            sb.Append($" alliance={GameVars.playeralliance} level={GamePrefs.leveltoload}/{GamePrefs.subleveltoload} latest={Saving.latestfolder}");
            if (GameApi.InCampaignScene)
                sb.Append($" date={GameApi.CampaignDateString()} float={GameApi.CampaignDate:F5} speed={GameVars.gamespeed} paused={GameVars.gamepaused} units={BattleUnits.completeunitlist?.Count}");
            sb.Append(" session=" + Session.Describe());
            sb.Append($" skippedSecondFinish={Patches.FinishLandBattleTwice.Skipped}");
            return sb.ToString();
        }

        private static void AsUi(Action a)
        {
            Battle.BattleSync.InUi = true;
            try { a(); }
            finally { Battle.BattleSync.InUi = false; }
        }

        private static string BattleStatus()
        {
            var bu = GameApi.BU;
            var bui = GameApi.BUI;
            if (bu == null || bu.IsCampaign) return "not in a battle; " + Status();
            return $"op={GameApi.Operation} frame={GameVars.frame} link={Battle.BattleLink.Describe()} rules: host={Battle.BattleRules.HostCommands} guest={Battle.BattleRules.GuestCommands} " +
                   $"deploying={bui?.deploymentmodeactive} eod={bu.eodcycle} day={bu.battlepasseddays} endbattle={bu.endbattle} hour={bu.uniStormSystem.Hour:F3} " +
                   $"speed={GameVars.gamespeed} paused={GameVars.gamepaused} shared={(Sync.ClockSync.SharedPaused ? "paused" : Sync.ClockSync.SharedSpeed + "x")} " +
                   $"drift={Sync.ClockSync.DriftHours * 60:F2}min ai0=side{bu.aibattle[0].sideofai}/p{bu.aibattle[0].isplayeraiorfeud} ai1=side{bu.aibattle[1].sideofai}/p{bu.aibattle[1].isplayeraiorfeud} " +
                   $"orders sent={Battle.BattleOrders.Sent} recv={Battle.BattleOrders.Received} applied={Battle.BattleOrders.Applied} ignored={Battle.BattleOrders.Ignored} " +
                   $"motion={Battle.BattleState.MotionReceived} stats={Battle.BattleState.StatsReceived} corrections={Battle.BattleState.Corrections} casualties={Battle.BattleState.CasualtyEvents} " +
                   $"drift avg={Battle.BattleState.AvgDrift:F2} max={Battle.BattleState.MaxDrift:F2} dupIds={Battle.BattleSync.DuplicateIds} roster matched={Battle.BattleSync.RosterMatched} unmatched={Battle.BattleSync.RosterUnmatched} " +
                   $"speedcalls timelogic={Patches.SharedSpeed.TimeLogicCalls} blocked={Patches.SharedSpeed.Blocked} buUpdate={Patches.HostBattleTimeLogic.Calls} uiUpdate={Patches.BattleUiInput.Calls} " +
                   $"prompt={GameApi.PH?.prompt_panelopen} hostPrompt={Battle.BattleState.HostPrompt} jumps={Battle.BattleSync.ClockJumps} deactivated={Battle.BattleState.Deactivated} inactiveMismatch={Battle.BattleState.InactiveMismatch} weather={GameApi.BFS?.currentweather}/{bu.uniStormSystem.weatherForecaster}";
        }

        private static string BattleUnitList(int alliance, int max)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var r in Battle.BattleSync.AllUnits())
            {
                if (alliance >= 0 && r.alliance != alliance) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                if (n++ >= max) continue;
                var p = r.GetPosition();
                sb.AppendLine($"{Battle.BattleSync.NetId(r)}\t{r.alliance}\t{r.unittyp}\t{r.name}\t{p.x:F1},{p.z:F1}\trot={r.transform.eulerAngles.y:F0}\tstr={r.strength}\tf={r.formation}\tpaths={r.regimentpaths}\tai={r.ai_stance}/{(r.aiassignedref != null ? "pool" : "-")}");
            }
            return n + " units\n" + sb;
        }

        private static string BattleUnitDetail(int id)
        {
            var r = Battle.BattleSync.Find(id);
            if (r == null) return "no such unit";
            var s = Battle.BattleOrderSnap.Capture(r);
            var p = r.GetPosition();
            var wps = string.Join(" ", s.Waypoints.Select((w, i) => $"({w.x:F1},{w.z:F1}:{s.PathStatus[i]})"));
            return $"{id} (local {r.unitid}) {r.name} all={r.alliance} typ={r.unittyp} pos=({p.x:F2},{p.z:F2}) rot={r.transform.eulerAngles.y:F1} str={r.strength} morale={r.morale:F2} " +
                   $"f={r.formation} cb={r.combatbehavior} ai={r.ai_stance} mm={r.movementmode} routed={r.isrouted} paths={r.regimentpaths} wps=[{wps}] queue={r.orderqueue.Count} " +
                   $"engineers={r.hasengineers}/{r.hasengineers2} active={r.gameObject.activeInHierarchy} building={(r.pontoonconstruction != null ? "pontoon" : r.obstacleconstruction != null ? "obstacle" : "-")}";
        }

        private static string UnitDetail(int id)
        {
            var r = GameApi.FindUnit(id);
            if (r == null) return "no such unit";
            var s = Sync.OrderSnap.Capture(r);
            var p = r.transform.position;
            var wps = string.Join(" ", s.Waypoints.Select(w => $"({w.x:F1},{w.z:F1})"));
            return $"{r.unitid} {r.name} all={r.alliance} typ={r.unittyp} pos=({p.x:F2},{p.z:F2}) str={r.groupstrength} inbattle={r.inbattle} retreat={r.onretreat} " +
                   $"paths={r.regimentpaths} wps=[{wps}] stance={s.Stance}/{s.StanceOrdered} rails={s.UseRails} sea={s.UseSea} rivers={s.UseRivers}";
        }

        private static string Units(int alliance)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var r in GameApi.Units())
            {
                if (alliance >= 0 && r.alliance != alliance) continue;
                if (r.unittyp < 14) continue; // only campaign-level formations (brigade and up)
                var p = r.transform.position;
                sb.AppendLine($"{r.unitid}\t{r.alliance}\t{r.unittyp}\t{r.name}\t{p.x:F1},{p.z:F1}\tstr={r.groupstrength}");
                n++;
            }
            return n + " units\n" + sb;
        }
    }
}
