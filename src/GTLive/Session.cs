using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using GTLive.Net;
using GTLive.Sync;
using UnityEngine;

namespace GTLive
{
    public enum SessionState { Off, Hosting, Connecting, Handshake, Syncing, Loading, WaitingPeer, Live }

    public enum BattleMode : byte { Autoresolve = 0, Ask = 1 }

    /// <summary>
    /// Connection and session lifecycle.
    ///   Host: running campaign -> Hosting -> guest says Hello -> Welcome -> save, send, both load the same files
    ///   -> both report Ready -> Go -> Live (shared clock, order sync, unit state stream).
    ///   Resyncs (manual, after a played battle) repeat the save/send/load step.
    /// </summary>
    public static class Session
    {
        public const int Protocol = 3;
        public const string HostFolder = "GTLive_Host";
        public const string GuestFolder = "GTLive_Guest";

        public static SessionState State = SessionState.Off;
        public static string RemoteName = "";
        public static string LastError = "";
        public static BattleMode Battles = BattleMode.Ask;
        public static readonly List<string> Notices = new List<string>();

        private static Listener _listener;
        private static Link _link;
        private static Connector _connector;

        private static string _level, _sub;
        private static MemoryStream _incoming;
        private static int _incomingExpected;
        private static string _syncReason;
        private static bool _localReady, _remoteReady;
        private static float _nextPing, _pingSentAt;
        private static float _lastPrefsCheck;
        private static AutoPrefs _sentPrefs;
        private static bool _hostAwayFromCampaign;
        private static float _backSince = -1f;

        public static bool IsLive => State == SessionState.Live;
        public static Link Link => _link;

        public static string Describe() =>
            $"{State} role={Mp.Role} active={Mp.Active} remote='{RemoteName}' host={Mp.HostAlliance} guest={Mp.GuestAlliance}"
            + (_link != null ? $" in={_link.BytesIn / 1024}KB out={_link.BytesOut / 1024}KB rtt={ClockSync.RttSeconds * 1000f:0}ms" : "")
            + (LastError.Length > 0 ? " lastError=" + LastError : "");

        public static void Send(MsgType t, byte[] payload) => _link?.Send(t, payload);

        public static void Notice(string text)
        {
            Notices.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            if (Notices.Count > 12) Notices.RemoveAt(0);
            Plugin.Log.LogInfo(text);
            Lab.Out("notice: " + text);
        }

        // ---- user actions ----------------------------------------------------------------------------

        public static void StartHost(int port)
        {
            if (State != SessionState.Off) throw new InvalidOperationException("already in a session");
            if (!GameApi.CampaignReady) throw new InvalidOperationException("load a campaign first");
            if (GameVars.playeralliance > 1) throw new InvalidOperationException("host must play the Union or the Confederacy");
            if (DLC_WL.dlc_scenarioactive) throw new InvalidOperationException("Whiskey & Lemons career campaigns can't be played in multiplayer; load a regular campaign");
            _listener = new Listener(port);
            Mp.Role = Role.Host;
            Mp.HostAlliance = GameVars.playeralliance;
            Mp.GuestAlliance = 1 - Mp.HostAlliance;
            _level = GameApi.CurrentLevel;
            _sub = GameApi.CurrentSublevel;
            State = SessionState.Hosting;
            LastError = "";
            Notice($"Hosting on port {port} as {AllianceName(Mp.HostAlliance)}. Waiting for a guest...");
        }

        public static void Join(string host, int port)
        {
            if (State != SessionState.Off) throw new InvalidOperationException("already in a session");
            _connector = new Connector(host, port);
            Mp.Role = Role.Guest;
            State = SessionState.Connecting;
            LastError = "";
            Notice($"Connecting to {host}:{port}...");
        }

        public static void Leave(string reason)
        {
            if (_link != null && !_link.Closed)
            {
                _link.Send(MsgType.Bye, Wire.Build(w => w.WriteStr(reason)));
                _link.Drain(300);
                _link.Close(reason);
            }
            _link = null;
            _listener?.Dispose();
            _listener = null;
            _connector = null;
            bool wasActive = Mp.Active;
            Mp.Active = false;
            Mp.Role = Role.None;
            State = SessionState.Off;
            _incoming = null;
            if (wasActive)
                Notice("Session ended (" + reason + "). The other side has no AI in this campaign: reload a save to keep playing alone.");
        }

        public static void Shutdown()
        {
            if (State != SessionState.Off) Leave("game closed");
        }

        /// <summary>Host: re-send the full campaign and have the guest reload it.</summary>
        public static void RequestResync(string reason)
        {
            if (Mp.Role == Role.Host)
            {
                if (State == SessionState.Live || State == SessionState.WaitingPeer) BeginSync(reason, reloadHost: false);
            }
            else if (State == SessionState.Live)
            {
                Send(MsgType.ResyncRequest, Wire.Build(w => w.WriteStr(reason)));
                Notice("Asked the host for a resync.");
            }
        }

        // ---- frame loop --------------------------------------------------------------------------------

        public static void Tick()
        {
            if (State == SessionState.Off) return;

            if (State == SessionState.Hosting && _listener != null)
            {
                if (_listener.Error != null) { LastError = _listener.Error; Leave("listener failed: " + _listener.Error); return; }
                if (_link == null && _listener.TryAccept(out var c))
                {
                    _link = new Link(c);
                    State = SessionState.Handshake;
                    Notice("Guest connected from " + _link.Remote);
                }
            }

            if (State == SessionState.Connecting && _connector != null && _connector.Done)
            {
                if (_connector.Result == null)
                {
                    LastError = _connector.Error ?? "connect failed";
                    Notice("Could not connect: " + LastError);
                    Leave("connect failed");
                    return;
                }
                _link = new Link(_connector.Result);
                _connector = null;
                State = SessionState.Handshake;
                Send(MsgType.Hello, Wire.Build(w =>
                {
                    w.Write(Protocol);
                    w.WriteStr(Plugin.Version);
                    w.WriteStr(Plugin.CfgPlayerName.Value);
                    w.Write(MainMenuControls.currentversion);
                    w.Write(DLC_WL.dlc_active);
                }));
            }

            if (_link != null)
            {
                Pump();
                if (_link != null && _link.Closed)
                {
                    LastError = _link.CloseReason;
                    OnDisconnected(_link.CloseReason);
                    return;
                }
                if (_link != null && Time.unscaledTime >= _nextPing)
                {
                    _nextPing = Time.unscaledTime + 2f;
                    _pingSentAt = Time.unscaledTime;
                    Send(MsgType.Ping, Wire.Build(w => w.Write(_pingSentAt)));
                }
                if (_link != null && (DateTime.UtcNow - _link.LastReceived).TotalSeconds > 30 && State != SessionState.Syncing && State != SessionState.Loading)
                {
                    _link.Close("timed out");
                }
            }

            if (State == SessionState.Loading && GameApi.CampaignReady)
                OnLocalCampaignLoaded();

            if (State == SessionState.Live) LiveTick();
        }

        private static void LiveTick()
        {
            bool inCampaign = GameApi.CampaignReady;
            if (Battle.BattleLink.Active)
            {
                Battle.BattleLink.Tick();
                if (Battle.BattleLink.HostBackOnCampaign)
                {
                    // the 3D battle is over and its result is on the host's campaign map: bring the guest along
                    Battle.BattleLink.Reset();
                    BeginSync("battle finished", reloadHost: false);
                }
                return;
            }
            if (Mp.Role == Role.Host)
            {
                if (!inCampaign)
                {
                    if (!_hostAwayFromCampaign && GameApi.Operation != 2)
                    {
                        _hostAwayFromCampaign = true;
                        ClockSync.SetShared(true, ClockSync.SharedSpeed);
                        Send(MsgType.Clock, Wire.Build(w => { w.Write(int.MaxValue - 1); w.Write(ClockSync.CampaignHours()); w.Write(true); w.Write(ClockSync.SharedSpeed); }));
                        SendNotice("The host is fighting a battle. The campaign resyncs when it ends.");
                    }
                    return;
                }
                if (_hostAwayFromCampaign)
                {
                    // back on the campaign map after a played battle: the guest needs the result,
                    // once the game has settled it (captured and destroyed formations go over the first frames)
                    if (_backSince < 0f) _backSince = Time.unscaledTime;
                    if (Time.unscaledTime - _backSince < 4f) return;
                    _backSince = -1f;
                    _hostAwayFromCampaign = false;
                    BeginSync("battle finished", reloadHost: false);
                    return;
                }
                ClockSync.HostTick();
                StructureSync.HostTick();
                CommanderSync.HostTick(force: false);
                Orders.Tick();
                UnitStates.HostTick();
                EconomySync.HostTick();
                WorldSync.HostTick();
                WeatherSync.HostTick();
                MonumentSync.HostTick();
                VesselSync.HostTick();
            }
            else
            {
                if (!inCampaign) return;
                ClockSync.GuestTick();
                Orders.Tick();
                EconomySync.GuestTick();
                WorldSync.GuestTick();
            }
            PrefsTick();
        }

        private static void PrefsTick()
        {
            if (Time.unscaledTime < _lastPrefsCheck + 1f) return;
            _lastPrefsCheck = Time.unscaledTime;
            var now = AutoPrefs.FromGame();
            if (_sentPrefs != null && _sentPrefs.SameAs(now)) return;
            _sentPrefs = now;
            Send(MsgType.Prefs, Wire.Build(w =>
            {
                w.Write(now.Finances); w.Write(now.ConstructionP); w.Write(now.ConstructionG); w.Write(now.ConstructionR);
                w.Write(now.Weapons); w.Write(now.Projects); w.Write(now.Policies); w.Write(now.PlaceBuildings);
            }));
        }

        private static void Pump()
        {
            int budget = 400;
            while (_link != null && budget-- > 0 && _link.TryReceive(out var p))
            {
                try { Handle(p); }
                catch (Exception e) { Plugin.Log.LogError($"handling {p.Type}: {e}"); }
            }
        }

        private static void Handle(Packet p)
        {
            var r = Wire.Reader(p.Data);
            switch (p.Type)
            {
                case MsgType.Ping:
                    Send(MsgType.Pong, p.Data);
                    break;
                case MsgType.Pong:
                    ClockSync.RttSeconds = Mathf.Lerp(ClockSync.RttSeconds, Time.unscaledTime - r.ReadSingle(), 0.3f);
                    break;
                case MsgType.Bye:
                    Notice("The other player left: " + r.ReadString());
                    OnDisconnected("remote left");
                    break;
                case MsgType.Hello:
                    OnHello(r);
                    break;
                case MsgType.Welcome:
                    OnWelcome(r);
                    break;
                case MsgType.Reject:
                    LastError = r.ReadString();
                    Notice("Host refused: " + LastError);
                    Leave("refused");
                    break;
                case MsgType.SaveBegin:
                    _level = r.ReadString();
                    _sub = r.ReadString();
                    _syncReason = r.ReadString();
                    _incomingExpected = r.ReadInt32();
                    _incoming = new MemoryStream(_incomingExpected);
                    State = SessionState.Syncing;
                    Mp.Active = true;
                    Notice($"Receiving campaign ({_incomingExpected / 1024} KB, {_syncReason})...");
                    break;
                case MsgType.SaveChunk:
                    _incoming?.Write(p.Data, 0, p.Data.Length);
                    break;
                case MsgType.SaveEnd:
                    OnSaveReceived();
                    break;
                case MsgType.Ready:
                    _remoteReady = true;
                    TryGo();
                    break;
                case MsgType.Go:
                    if (Mp.Role == Role.Guest) GoLive();
                    break;
                case MsgType.Clock:
                    if (Mp.Role == Role.Guest) ClockSync.OnClock(r);
                    break;
                case MsgType.SpeedRequest:
                    if (Mp.Role == Role.Host && State == SessionState.Live) ClockSync.OnSpeedRequest(r);
                    break;
                case MsgType.OrderRequest:
                    if (Mp.Role == Role.Host && State == SessionState.Live && GameApi.CampaignReady) Orders.OnRequest(r);
                    break;
                case MsgType.OrderState:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) Orders.OnState(r);
                    break;
                case MsgType.UnitState:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) UnitStates.OnState(r);
                    break;
                case MsgType.Strength:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) UnitStates.OnStrength(r);
                    break;
                case MsgType.Economy:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) EconomySync.OnEconomy(r);
                    break;
                case MsgType.Decisions:
                    if (Mp.Role == Role.Host && State == SessionState.Live && GameApi.CampaignReady) EconomySync.OnDecisions(r);
                    break;
                case MsgType.Commanders:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) CommanderSync.OnCommanders(r);
                    break;
                case MsgType.UnitCreate:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) StructureSync.OnCreate(r);
                    break;
                case MsgType.UnitDelete:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) StructureSync.OnDelete(r);
                    break;
                case MsgType.UnitReparent:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) StructureSync.OnReparent(r);
                    break;
                case MsgType.StructureRequest:
                    if (Mp.Role == Role.Host && State == SessionState.Live && GameApi.CampaignReady) StructureSync.OnRequest(r);
                    break;
                case MsgType.World:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) WorldSync.OnWorld(r);
                    break;
                case MsgType.GuestAction:
                    if (Mp.Role == Role.Host && State == SessionState.Live && GameApi.CampaignReady) GuestActions.OnAction(r);
                    break;
                case MsgType.Weather:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) WeatherSync.OnWeather(p.Data);
                    break;
                case MsgType.Monuments:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) MonumentSync.OnMonuments(r);
                    break;
                case MsgType.BattlePrompt:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) Battle.BattleDecision.OnPrompt(r);
                    break;
                case MsgType.BattleChoice:
                    if (Mp.Role == Role.Host && State == SessionState.Live) Battle.BattleDecision.OnChoice(r);
                    break;
                case MsgType.BattleResolved:
                    if (Mp.Role == Role.Guest && State == SessionState.Live) Battle.BattleDecision.OnResolved(r);
                    break;
                case MsgType.BattleTransferBegin:
                    if (Mp.Role == Role.Guest) Battle.BattleLink.OnTransferBegin(r);
                    break;
                case MsgType.BattleTransferChunk:
                    if (Mp.Role == Role.Guest) Battle.BattleLink.OnTransferChunk(p.Data);
                    break;
                case MsgType.BattleTransferEnd:
                    if (Mp.Role == Role.Guest) Battle.BattleLink.OnTransferEnd();
                    break;
                case MsgType.BattleReady:
                    if (Mp.Role == Role.Host) Battle.BattleLink.OnPeerReady();
                    break;
                case MsgType.BattleGo:
                    if (Mp.Role == Role.Guest) Battle.BattleLink.OnGo();
                    break;
                case MsgType.BattleState:
                case MsgType.BattleOrder:
                case MsgType.BattleOrderState:
                case MsgType.BattleEvent:
                    Battle.BattleSync.Handle(p.Type, r);
                    break;
                case MsgType.BattleFight:
                    if (Mp.Role == Role.Host && State == SessionState.Live && GameApi.CampaignReady) Battle.BattleFight.OnGuestFight(r);
                    break;
                case MsgType.Vessels:
                    if (Mp.Role == Role.Guest && State == SessionState.Live && GameApi.CampaignReady) VesselSync.OnVessels(r);
                    break;
                case MsgType.VesselRequest:
                    if (Mp.Role == Role.Host && State == SessionState.Live && GameApi.CampaignReady) VesselSync.OnRequest(r);
                    break;
                case MsgType.ResyncRequest:
                    if (Mp.Role == Role.Host) { Notice("Guest asked for a resync: " + r.ReadString()); RequestResync("guest request"); }
                    break;
                case MsgType.Notice:
                case MsgType.Chat:
                    Notice(r.ReadString());
                    break;
                case MsgType.Prefs:
                    var pr = Mp.RemotePrefs;
                    pr.Finances = r.ReadBoolean(); pr.ConstructionP = r.ReadBoolean(); pr.ConstructionG = r.ReadBoolean(); pr.ConstructionR = r.ReadBoolean();
                    pr.Weapons = r.ReadBoolean(); pr.Projects = r.ReadBoolean(); pr.Policies = r.ReadBoolean(); pr.PlaceBuildings = r.ReadBoolean();
                    break;
            }
        }

        public static void SendNotice(string text)
        {
            Notice(text);
            Send(MsgType.Notice, Wire.Build(w => w.WriteStr(text)));
        }

        // ---- handshake ---------------------------------------------------------------------------------

        private static void OnHello(BinaryReader r)
        {
            if (Mp.Role != Role.Host) return;
            int proto = r.ReadInt32();
            string version = r.ReadString();
            RemoteName = r.ReadString();
            float gameVersion = r.ReadSingle();
            bool dlc = r.ReadBoolean();
            string problem = null;
            if (proto != Protocol)
                problem = $"mod version mismatch: host has GTLive {Plugin.Version}, you have {version}";
            else if (Mathf.Abs(gameVersion - MainMenuControls.currentversion) > 0.00001f)
                problem = $"game version mismatch: host runs {MainMenuControls.currentversion}, you run {gameVersion}. Update both games in Steam.";
            else if (dlc != DLC_WL.dlc_active)
                problem = "one of you has the Whiskey & Lemons DLC and the other doesn't; the campaign data differs";
            if (problem != null)
            {
                Notice($"{RemoteName} could not join: {problem}");
                Send(MsgType.Reject, Wire.Build(w => w.WriteStr(problem)));
                _link.Drain(300);
                _link.Close("rejected");
                return;
            }
            if (!GameApi.CampaignReady)
            {
                Send(MsgType.Reject, Wire.Build(w => w.WriteStr("host is not on the campaign map right now")));
                _link.Drain(300);
                _link.Close("host busy");
                return;
            }
            Send(MsgType.Welcome, Wire.Build(w =>
            {
                w.Write(Mp.HostAlliance);
                w.Write(Mp.GuestAlliance);
                w.WriteStr(Plugin.CfgPlayerName.Value);
                w.Write((byte)Battles);
            }));
            Notice($"{RemoteName} joined and will play {AllianceName(Mp.GuestAlliance)}.");
            BeginSync("session start", reloadHost: true);
        }

        private static void OnWelcome(BinaryReader r)
        {
            Mp.HostAlliance = r.ReadInt32();
            Mp.GuestAlliance = r.ReadInt32();
            RemoteName = r.ReadString();
            Battles = (BattleMode)r.ReadByte();
            Notice($"Joined {RemoteName}'s campaign as {AllianceName(Mp.GuestAlliance)}.");
        }

        // ---- full sync -----------------------------------------------------------------------------------

        private static void BeginSync(string reason, bool reloadHost)
        {
            _syncReason = reason;
            _localReady = _remoteReady = false;
            Mp.Active = true;
            ClockSync.SetShared(true, ClockSync.SharedSpeed);
            Mp.Applying = true;
            try { GameApi.SetSpeed(0f); } finally { Mp.Applying = false; }

            State = SessionState.Syncing;
            string dir = GameApi.SaveCampaignTo(HostFolder, "GTLive multiplayer (last sync)");
            byte[] zip = ZipFolder(dir);
            Notice($"Sending campaign to {RemoteName} ({zip.Length / 1024} KB, {reason})...");
            Send(MsgType.SaveBegin, Wire.Build(w =>
            {
                w.WriteStr(_level);
                w.WriteStr(_sub);
                w.WriteStr(reason);
                w.Write(zip.Length);
            }));
            const int chunk = 256 * 1024;
            for (int off = 0; off < zip.Length; off += chunk)
            {
                int n = Math.Min(chunk, zip.Length - off);
                var part = new byte[n];
                Buffer.BlockCopy(zip, off, part, 0, n);
                Send(MsgType.SaveChunk, part);
            }
            Send(MsgType.SaveEnd, new byte[0]);

            if (reloadHost)
            {
                State = SessionState.Loading;
                GameApi.LoadCampaignSave(_level, _sub, HostFolder, Mp.HostAlliance);
            }
            else
            {
                OnLocalCampaignLoaded();
            }
        }

        private static void OnSaveReceived()
        {
            if (_incoming == null) return;
            var bytes = _incoming.ToArray();
            _incoming = null;
            if (bytes.Length != _incomingExpected)
            {
                Leave($"campaign transfer incomplete ({bytes.Length}/{_incomingExpected} bytes)");
                return;
            }
            string dir = Path.Combine(GameApi.CampaignFolder(_level, _sub), GuestFolder);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(Path.GetTempPath(), "gtlive_in_" + Plugin.InstanceId + ".zip");
            File.WriteAllBytes(tmp, bytes);
            ZipFile.ExtractToDirectory(tmp, dir);
            File.Delete(tmp);
            Notice("Campaign received, loading...");
            State = SessionState.Loading;
            Mp.Active = true;
            _localReady = _remoteReady = false;
            GameApi.LoadCampaignSave(_level, _sub, GuestFolder, Mp.GuestAlliance);
        }

        private static byte[] ZipFolder(string dir)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gtlive_out_" + Plugin.InstanceId + ".zip");
            if (File.Exists(tmp)) File.Delete(tmp);
            ZipFile.CreateFromDirectory(dir, tmp, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: false);
            var bytes = File.ReadAllBytes(tmp);
            File.Delete(tmp);
            return bytes;
        }

        private static void OnLocalCampaignLoaded()
        {
            Mp.Applying = true;
            try { GameApi.SetSpeed(0f); } finally { Mp.Applying = false; }
            Battle.BattleLink.Reset();
            Battle.BattleDecision.Reset();
            Orders.Reset();
            Orders.Baseline();
            UnitStates.Reset();
            EconomySync.Reset();
            StructureSync.Reset();
            CommanderSync.Reset();
            WorldSync.Reset();
            WeatherSync.Reset();
            MonumentSync.Reset();
            VesselSync.Reset();
            ClockSync.Reset();
            _sentPrefs = null;
            _localReady = true;
            State = SessionState.WaitingPeer;
            Send(MsgType.Ready, new byte[0]);
            Notice("Campaign loaded, waiting for " + (string.IsNullOrEmpty(RemoteName) ? "the other player" : RemoteName) + "...");
            TryGo();
        }

        private static void TryGo()
        {
            if (Mp.Role != Role.Host || !_localReady || !_remoteReady || State != SessionState.WaitingPeer) return;
            Send(MsgType.Go, new byte[0]);
            GoLive();
        }

        private static void GoLive()
        {
            State = SessionState.Live;
            ClockSync.SetShared(true, 1f);
            Notice("Live! The campaign is paused; either player can unpause.");
        }

        private static void OnDisconnected(string reason)
        {
            _link?.Close(reason);
            _link = null;
            if (Mp.Role == Role.Host && _listener != null)
            {
                // keep the campaign in multiplayer rules so the same guest can reconnect
                State = SessionState.Hosting;
                ClockSync.SetShared(true, ClockSync.SharedSpeed);
                Mp.Applying = true;
                try { if (GameApi.CampaignReady) GameApi.SetSpeed(0f); } finally { Mp.Applying = false; }
                Notice("Guest disconnected (" + reason + "). Paused; waiting for them to rejoin.");
            }
            else
            {
                Leave("disconnected: " + reason);
            }
        }

        public static string AllianceName(int a)
        {
            try { return GameVars.alliance != null && a >= 0 && a < GameVars.alliance.Length ? GameVars.alliance[a].name : (a == 0 ? "Union" : "Confederacy"); }
            catch { return a == 0 ? "Union" : "Confederacy"; }
        }

        // ---- lab ---------------------------------------------------------------------------------------

        public static string LabCommand(string[] a)
        {
            if (a.Length == 0) return Describe();
            switch (a[0])
            {
                case "host":
                    if (a.Contains("autores")) Battles = BattleMode.Autoresolve; else Battles = BattleMode.Ask;
                    StartHost(a.Length > 1 && int.TryParse(a[1], out int port) ? port : Plugin.CfgPort.Value);
                    return Describe() + " battles=" + Battles;
                case "join":
                    Join(a.Length > 1 ? a[1] : Plugin.CfgHostAddress.Value, a.Length > 2 ? int.Parse(a[2]) : Plugin.CfgPort.Value);
                    return Describe();
                case "leave":
                    Leave("lab");
                    return Describe();
                case "resync":
                    RequestResync("lab");
                    return Describe();
                case "speed":
                    ClockSync.LocalInput(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) <= 0f, float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
                    return $"shared paused={ClockSync.SharedPaused} speed={ClockSync.SharedSpeed}";
                case "stats":
                    return Describe() + "\n" + Stats();
                default:
                    return "session host|join|leave|resync|speed <x>|stats";
            }
        }

        public static string Stats() =>
            $"clock: hours={ClockSync.CampaignHours():F2} drift={ClockSync.DriftHours:F3}h rate={ClockSync.RateFactor:F2} paused={ClockSync.SharedPaused} speed={ClockSync.SharedSpeed}\n" +
            $"orders: sent={Orders.Sent} recv={Orders.Received} applied={Orders.Applied} ignored={Orders.Ignored}\n" +
            $"units: {UnitStates.Summary()}\n" +
            $"economy: pushes={EconomySync.Pushes} applies={EconomySync.Applies}\n" +
            $"structure: created={StructureSync.Created} deleted={StructureSync.Deleted} reparented={StructureSync.Reparented} requests={StructureSync.Requests} commanders sent={CommanderSync.Sent} applied={CommanderSync.Applied} count={GameVars.commander?.Count}\n" +
            $"world: changes={WorldSync.Changes} buildings={BattleUnits.cbuildings?.Count}  guest actions sent={GuestActions.Sent} applied={GuestActions.Applied}\n" +
            $"map: weather pushes={WeatherSync.Pushes} applies={WeatherSync.Applies} monuments={MonumentSync.Updates} (now {GameApi.BU?.GetComponent<BattlefieldSetup>()?.battlemonument?.Count}) ships changes={VesselSync.Changes} count={BattleUnits.GetCompleteVesselList().Count}";
    }
}
