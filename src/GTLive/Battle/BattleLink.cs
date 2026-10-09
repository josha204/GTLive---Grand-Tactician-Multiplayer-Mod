using System;
using System.IO;
using System.IO.Compression;
using GTLive.Net;
using GTLive.Sync;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// Getting both players into the same 3D battle, and back out.
    ///   Host: launches the battle the normal way; once its battle scene is ready it writes a battle save
    ///         (campaign autosave + Battle subfolder, like the game's own "save during a campaign battle") into
    ///         GTLive_Battle and sends it to the guest.
    ///   Guest: unpacks it into GTLive_Guest and loads it through the game's "load campaign battle from save" path,
    ///         as its own alliance.
    ///   Both ready -> Go -> BattleSync runs until the host leaves the battle; the host then resyncs the campaign.
    /// </summary>
    public static class BattleLink
    {
        public enum Phase { None, Launching, Receiving, Loading, WaitingPeer, Live }

        public const string HostFolder = "GTLive_Battle";
        public const string GuestFolder = "GTLive_Guest";

        public static Phase State = Phase.None;
        public static bool HostPlays, GuestPlays;
        public static bool Active => State != Phase.None;
        public static bool IsLive => State == Phase.Live;
        private static bool _localReady, _remoteReady, _wasInBattle;
        private static MemoryStream _incoming;
        private static int _expected;
        private static string _campaignScene, _battleLevel, _battleSub;

        public static string Describe() => $"{State} hostPlays={HostPlays} guestPlays={GuestPlays}";

        public static bool BattleSceneReady =>
            GameApi.BU != null && !GameApi.BU.IsCampaign && SceneManagement.currentoperation == 3
            && GameVars.allregimentsinitialized && GameVars.frame > 45;

        public static void Prepare(bool hostPlays, bool guestPlays)
        {
            HostPlays = hostPlays;
            GuestPlays = guestPlays;
            _localReady = _remoteReady = _wasInBattle = false;
            State = Mp.Role == Role.Host ? Phase.Launching : Phase.Receiving;
            BattleSync.Reset();
            BattleRules.Begin(hostPlays, guestPlays);
        }

        public static void Reset()
        {
            State = Phase.None;
            _incoming = null;
            BattleSync.Reset();
            BattleRules.End();
        }

        /// <summary>The player whose army is driven by the battle AI on the host (if any).</summary>
        public static bool LocalPlays => Mp.Role == Role.Host ? HostPlays : GuestPlays;

        public static void Tick()
        {
            if (BattleSceneReady) _wasInBattle = true;

            if (Mp.Role == Role.Host)
            {
                if (State == Phase.Launching && BattleSceneReady)
                {
                    SendBattle();
                    _localReady = true;
                    State = Phase.WaitingPeer;
                    TryGo();
                }
            }
            else if (State == Phase.Loading && BattleSceneReady)
            {
                _localReady = true;
                State = Phase.WaitingPeer;
                Session.Send(MsgType.BattleReady, new byte[0]);
                Session.Notice("Battle loaded, waiting for " + Session.RemoteName + "...");
            }

            if (State == Phase.Live) BattleSync.Tick();
        }

        private static float _campaignSince = -1f;

        /// <summary>
        /// Host: true once the battle is over and the host has been back on the campaign map for a few seconds
        /// (the game settles the battle's results - captured and destroyed formations - over the first frames).
        /// </summary>
        public static bool HostBackOnCampaign
        {
            get
            {
                if (Mp.Role != Role.Host || !_wasInBattle || !GameApi.CampaignReady) { _campaignSince = -1f; return false; }
                if (_campaignSince < 0f) _campaignSince = Time.unscaledTime;
                return Time.unscaledTime - _campaignSince > 4f;
            }
        }

        // ---- host -------------------------------------------------------------------------------------

        private static void SendBattle()
        {
            string scen = SceneManagement.currentcampaignscenario;
            string root = Plugin.GameRoot;
            string src = Path.Combine(root, "Campaigns", scen, Saving.latestfolder);
            string dst = Path.Combine(root, "Campaigns", scen, HostFolder);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            CopyDir(src, dst, skipBattle: true);

            string prev = Saving.latestfolder;
            try
            {
                Saving.latestfolder = HostFolder;
                ImportExportUnitData.SaveBattle(isautosave: false, manualsavename: "GTLive battle");
            }
            finally { Saving.latestfolder = prev; }

            string tmp = Path.Combine(Path.GetTempPath(), "gtlive_battle_" + Plugin.InstanceId + ".zip");
            if (File.Exists(tmp)) File.Delete(tmp);
            ZipFile.CreateFromDirectory(dst, tmp, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: false);
            byte[] zip = File.ReadAllBytes(tmp);
            File.Delete(tmp);

            string level = GamePrefs.leveltoload, sub = GamePrefs.subleveltoload;
            bool hp = HostPlays, gp = GuestPlays;
            Session.Send(MsgType.BattleTransferBegin, Wire.Build(w =>
            {
                w.WriteStr(scen);
                w.WriteStr(level);
                w.WriteStr(sub);
                w.Write(hp);
                w.Write(gp);
                w.Write(zip.Length);
            }));
            const int chunk = 256 * 1024;
            for (int off = 0; off < zip.Length; off += chunk)
            {
                int n = Math.Min(chunk, zip.Length - off);
                var part = new byte[n];
                Buffer.BlockCopy(zip, off, part, 0, n);
                Session.Send(MsgType.BattleTransferChunk, part);
            }
            Session.Send(MsgType.BattleTransferEnd, new byte[0]);
            Session.Notice($"Battle loaded. Sending it to {Session.RemoteName} ({zip.Length / 1024} KB)...");
        }

        private static void CopyDir(string src, string dst, bool skipBattle)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
            {
                if (skipBattle && Path.GetFileName(d).Equals("Battle", StringComparison.OrdinalIgnoreCase)) continue;
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), false);
            }
        }

        public static void OnPeerReady()
        {
            _remoteReady = true;
            TryGo();
        }

        private static void TryGo()
        {
            if (Mp.Role != Role.Host || !_localReady || !_remoteReady || State != Phase.WaitingPeer) return;
            Session.Send(MsgType.BattleGo, new byte[0]);
            GoLive();
        }

        private static void GoLive()
        {
            State = Phase.Live;
            BattleSync.Start();
            Session.Notice("Both armies are on the field. " + BattleDecision.Roles(HostPlays, GuestPlays));
        }

        // ---- guest ------------------------------------------------------------------------------------

        public static void OnTransferBegin(BinaryReader r)
        {
            _campaignScene = r.ReadString();
            _battleLevel = r.ReadString();
            _battleSub = r.ReadString();
            HostPlays = r.ReadBoolean();
            GuestPlays = r.ReadBoolean();
            BattleRules.Begin(HostPlays, GuestPlays);
            _expected = r.ReadInt32();
            _incoming = new MemoryStream(_expected);
            State = Phase.Receiving;
            Session.Notice($"Receiving the battle ({_expected / 1024} KB)...");
        }

        public static void OnTransferChunk(byte[] data) => _incoming?.Write(data, 0, data.Length);

        public static void OnTransferEnd()
        {
            if (_incoming == null) return;
            byte[] bytes = _incoming.ToArray();
            _incoming = null;
            if (bytes.Length != _expected) { Session.Notice("Battle transfer incomplete."); return; }
            string dir = Path.Combine(Plugin.GameRoot, "Campaigns", _campaignScene, GuestFolder);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(Path.GetTempPath(), "gtlive_battle_in_" + Plugin.InstanceId + ".zip");
            File.WriteAllBytes(tmp, bytes);
            ZipFile.ExtractToDirectory(tmp, dir);
            File.Delete(tmp);
            LoadBattleFromSave();
        }

        /// <summary>Saving.LoadSave's "campaign battle" branch, as the guest's alliance.</summary>
        private static void LoadBattleFromSave()
        {
            int op = SceneManagement.currentoperation;
            if (op == 1) SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_closebattlescenario, 0, false));
            if (op == 2 || op == 3) SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_exitcampaignscenario, 0, false));
            Saving.latestfolder = GuestFolder;
            Saving.priorloadedfolder = GuestFolder;
            SceneManagement.currentoperation = 8;
            ImportExportUnitData.LoadScenarioFile("Campaigns/" + _campaignScene + "/" + GuestFolder + "/Battle/", op != 0);
            GameVars.playeralliance = Mp.GuestAlliance;
            int pa = GameVars.playeralliance;
            object sound = LoadingScreen.sound_loadingbattle != null && pa < LoadingScreen.sound_loadingbattle.Length ? (object)LoadingScreen.sound_loadingbattle[pa] : null;
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_showblackscreen));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_rebuildscene, 1));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_resetloadingscreen, 2, 115, -1, -2, null, null, null, "", sound));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_loadcampaignbattlefromsave, 3, _battleLevel, _battleSub, _campaignScene));
            SceneManagement.batchoperation.Add(new SceneManagement.BatchOperation(SceneManagement.bo_hideblackscreen, 3));
            State = Phase.Loading;
            Session.Notice("Loading the battle...");
        }

        public static void OnGo()
        {
            if (Mp.Role == Role.Guest) GoLive();
        }
    }
}
