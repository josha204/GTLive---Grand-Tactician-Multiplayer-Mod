using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GTLive
{
    /// <summary>Thin wrappers over the game's static state. Everything here must run on the main thread.</summary>
    public static class GameApi
    {
        private static BattleUnits _bu;

        public static BattleUnits BU
        {
            get
            {
                if (_bu == null)
                {
                    var gc = GameObject.Find("GameController");
                    if (gc != null) _bu = gc.GetComponent<BattleUnits>();
                }
                return _bu;
            }
        }

        private static PanelHandler _ph;
        private static BattlefieldSetup _bfs;

        public static PanelHandler PH
        {
            get
            {
                if (_ph == null)
                {
                    var ui = GameObject.Find("UI");
                    if (ui != null) _ph = ui.GetComponent<PanelHandler>();
                }
                return _ph;
            }
        }

        public static BattlefieldSetup BFS
        {
            get
            {
                if (_bfs == null)
                {
                    var gc = GameObject.Find("GameController");
                    if (gc != null) _bfs = gc.GetComponent<BattlefieldSetup>();
                }
                return _bfs;
            }
        }

        private static TimePanel _tp;

        public static TimePanel TP
        {
            get
            {
                if (_tp == null)
                {
                    var go = GameObject.Find("UI/TimePanel");
                    if (go != null) _tp = go.GetComponent<TimePanel>();
                }
                return _tp;
            }
        }

        private static BattleUI _bui;

        public static BattleUI BUI
        {
            get
            {
                if (_bui == null)
                {
                    var gc = GameObject.Find("GameController");
                    if (gc != null) _bui = gc.GetComponent<BattleUI>();
                }
                return _bui;
            }
        }

        /// <summary>SceneManagement.currentoperation: 0 menu, 2 campaign running, 3/8 campaign battle, 4 launching battle, 5 back from battle, 6 loading save.</summary>
        public static int Operation => SceneManagement.currentoperation;

        public static bool InCampaignScene => BU != null && BU.IsCampaign;

        /// <summary>True once the campaign scene finished its staged frame-by-frame initialisation.</summary>
        public static bool CampaignReady =>
            InCampaignScene && Operation == 2 && GameVars.allregimentsinitialized && GameVars.frame > 55
            && BattleUnits.completeunitlist != null;

        public static float CampaignDate => GameVars.GetCampaignFloatingDate();

        public static string CampaignDateString()
        {
            var bu = BU;
            if (bu == null || bu.uniStormSystem == null) return "-";
            var u = bu.uniStormSystem;
            return $"{bu.year}-{u.monthCounter:00}-{u.dayCounter:00} {u.Hour:00.00}h";
        }

        public static string CurrentLevel => GamePrefs.leveltoload;
        public static string CurrentSublevel => GamePrefs.subleveltoload;

        public static string CampaignFolder(string level, string sub) =>
            Path.Combine(Plugin.GameRoot, "Campaigns", level, sub);

        /// <summary>Writes a full campaign save (same files the game writes) into Campaigns/lvl/sub/folder.</summary>
        public static string SaveCampaignTo(string folder, string manualName)
        {
            if (!InCampaignScene || Operation != 2)
                throw new InvalidOperationException("not in a running campaign (operation " + Operation + ")");
            string prev = Saving.latestfolder;
            try
            {
                Saving.latestfolder = folder;
                string dir = CampaignFolder(CurrentLevel, CurrentSublevel) + Path.DirectorySeparatorChar + folder;
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                SceneManagement.SaveAll(saveunits: true, saveobjects: true, savecommanders: true, savecampaigndata: true, isautosave: false, manualsavename: manualName ?? "");
                return dir;
            }
            finally
            {
                Saving.latestfolder = prev;
            }
        }

        /// <summary>
        /// Loads a campaign save the same way the load dialog does (Saving.LoadSave, num == 0 branch),
        /// but lets the caller choose which alliance the local player controls.
        /// </summary>
        public static void LoadCampaignSave(string level, string sub, string folder, int alliance)
        {
            string rel = "Campaigns/" + level + "/" + sub + "/" + folder + "/";
            if (!File.Exists(Path.Combine(Plugin.GameRoot, rel + "scenario.dat")))
                throw new FileNotFoundException("no scenario.dat in " + rel);

            int president = ReadPresident(Path.Combine(Plugin.GameRoot, rel + "scenario.dat"));
            int op = Operation;
            if (SceneManagement.batchoperation == null)
                SceneManagement.batchoperation = new List<SceneManagement.BatchOperation>();
            if (op == 1)
                SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_closebattlescenario, 0, false));
            if (op == 2 || op == 3)
                SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_exitcampaignscenario, 0, false));

            Saving.latestfolder = folder;
            Saving.priorloadedfolder = folder;
            SceneManagement.currentoperation = 6;
            ImportExportUnitData.LoadScenarioFile(rel, op != 0);
            GameVars.playeralliance = alliance;
            _bu = null;

            var sound = LoadingScreen.sound_loadingcampaign != null && alliance < LoadingScreen.sound_loadingcampaign.Length
                ? (object)LoadingScreen.sound_loadingcampaign[alliance] : null;
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_showblackscreen));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_rebuildscene, 1));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_resetloadingscreen, 2, 122, -2, president, null, null, null, "", sound));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_loadcampaignscenario, 2, level, sub, president));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_hideblackscreen, 2));
        }

        private static int ReadPresident(string scenarioFile)
        {
            var lines = File.ReadAllLines(scenarioFile);
            // line 11 (0-based) is CampaignStrategy.currentpresident, see ImportExportUnitData.SaveScenarioFile
            if (lines.Length > 11 && int.TryParse(lines[11], out var p)) return p;
            return -2;
        }

        public static void SetSpeed(float speed)
        {
            GameVars.SetGameSpeed(speed, overrideminmax: false, manualset: true);
        }

        public static IEnumerable<Regiment> Units()
        {
            var list = BattleUnits.completeunitlist;
            if (list == null) yield break;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) yield return list[i];
        }

        public static Regiment FindUnit(int unitid)
        {
            var list = BattleUnits.completeunitlist;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].unitid == unitid) return list[i];
            return null;
        }
    }
}
