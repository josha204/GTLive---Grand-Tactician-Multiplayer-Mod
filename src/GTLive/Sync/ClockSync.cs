using System;
using System.IO;
using GTLive.Net;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// One shared campaign clock. The host's clock is authoritative; the guest follows it by running its own clock
    /// slightly faster or slower (unit movement and every timed process key off the clock, not frame time).
    /// Only explicit player input (pause / speed buttons and hotkeys) changes the shared clock; the game's own
    /// automatic pauses (opening panels, Ctrl-path planning) are suppressed during a live session.
    /// </summary>
    public static class ClockSync
    {
        public static bool SharedPaused = true;
        public static float SharedSpeed = 1f;

        // set by TimePatches while the game's speed hotkey handler runs
        public static bool InKeyHandler;

        private static float _nextSend;
        private static bool _dirty;
        private static int _seq;

        // guest side
        public static double HostHours;          // host clock at the time the last Clock message was received
        public static float HostHoursAt;         // Time.unscaledTime of that reception
        public static double DriftHours;         // + means guest is behind
        public static float RateFactor = 1f;
        public static float RttSeconds = 0.05f;
        private static int _lastSeq = -1;

        public static void Reset()
        {
            SharedPaused = true;
            SharedSpeed = 1f;
            RateFactor = 1f;
            DriftHours = 0;
            _lastSeq = -1;
            _dirty = true;
        }

        /// <summary>Absolute campaign time in hours since 1860-01-01.</summary>
        public static double CampaignHours()
        {
            var bu = GameApi.BU;
            if (bu == null || bu.uniStormSystem == null) return 0;
            var u = bu.uniStormSystem;
            int year = Mathf.Max(bu.year, u.yearCounter);
            int month = Mathf.Clamp(u.monthCounter, 1, 12);
            int day = Mathf.Clamp(u.dayCounter, 1, DateTime.DaysInMonth(Mathf.Clamp(year, 1, 9999), month));
            double days = (new DateTime(year, month, day) - new DateTime(1860, 1, 1)).TotalDays;
            return days * 24.0 + u.Hour;
        }

        /// <summary>Campaign hours that pass per real second at the given speed.</summary>
        public static double HoursPerSecond(float speed) =>
            speed <= 0f ? 0 : 0.2 / Math.Max(0.0001, GamePrefs.battledaylengthinmins / speed);

        // ---- player input ------------------------------------------------------------------------------

        /// <summary>Called from the SetGameSpeed patch when the local player pauses or changes speed.</summary>
        public static void LocalInput(bool paused, float speed)
        {
            var speeds = GameApi.InCampaignScene ? GameVars.gamespeedcampaigns : GameVars.gamespeedbattles;
            if (!paused) speed = Mathf.Clamp(speed, GamePrefs.mingamespeed, speeds[speeds.Length - 1]);
            if (Mp.Role == Role.Host)
            {
                SetShared(paused, speed);
            }
            else
            {
                // predict locally, the host's next Clock message confirms or corrects it
                SetShared(paused, speed);
                Session.Send(MsgType.SpeedRequest, Wire.Build(w => { w.Write(paused); w.Write(speed); }));
            }
        }

        public static void SetShared(bool paused, float speed)
        {
            SharedPaused = paused;
            if (!paused && speed > 0f) SharedSpeed = speed;
            _dirty = true;
        }

        public static void OnSpeedRequest(BinaryReader r)
        {
            bool paused = r.ReadBoolean();
            float speed = r.ReadSingle();
            SetShared(paused, speed);
            Session.Notice(paused ? "Paused by " + Session.RemoteName : $"Speed set to {speed:0.#}x by {Session.RemoteName}");
        }

        // ---- per-frame --------------------------------------------------------------------------------

        public static void HostTick()
        {
            Enforce(1f);
            if (_dirty || Time.unscaledTime >= _nextSend) SendNow();
        }

        /// <summary>Host: sends the shared clock right away (e.g. before leaving the campaign map for a battle).</summary>
        public static void SendNow()
        {
            _dirty = false;
            _nextSend = Time.unscaledTime + 0.2f;
            int seq = ++_seq;
            double hours = CampaignHours();
            Session.Send(MsgType.Clock, Wire.Build(w =>
            {
                w.Write(seq);
                w.Write(hours);
                w.Write(SharedPaused);
                w.Write(SharedSpeed);
            }));
        }

        public static void OnClock(BinaryReader r)
        {
            int seq = r.ReadInt32();
            if (seq <= _lastSeq) return;
            _lastSeq = seq;
            HostHours = r.ReadDouble();
            HostHoursAt = Time.unscaledTime;
            SharedPaused = r.ReadBoolean();
            SharedSpeed = r.ReadSingle();
        }

        public static void GuestTick()
        {
            if (HostHoursAt > 0f)
            {
                double hps = SharedPaused ? 0 : HoursPerSecond(SharedSpeed);
                double hostNow = HostHours + (Time.unscaledTime - HostHoursAt + RttSeconds * 0.5) * hps;
                DriftHours = hostNow - CampaignHours();
                if (SharedPaused || hps <= 0)
                {
                    RateFactor = 1f;
                }
                else
                {
                    // close the gap over about two real seconds, within +-25%
                    double secondsBehind = DriftHours / hps;
                    RateFactor = Mathf.Clamp(1f + (float)(secondsBehind / 2.0), 0.75f, 1.33f);
                }
            }
            Enforce(RateFactor);
        }

        /// <summary>Makes the local game match the shared clock state.</summary>
        private static void Enforce(float rate)
        {
            if (!GameApi.CampaignReady && !Battle.BattleSync.ClockActive) return;
            bool needPause = SharedPaused && !GameVars.gamepaused;
            bool needRun = !SharedPaused && (GameVars.gamepaused || Mathf.Abs(GameVars.gamespeed - SharedSpeed) > 0.0001f);
            if (needPause || needRun)
            {
                Mp.Applying = true;
                try { GameVars.SetGameSpeed(SharedPaused ? 0f : SharedSpeed, overrideminmax: true, manualset: true); }
                finally { Mp.Applying = false; }
            }
            if (!SharedPaused)
            {
                var u = GameApi.BU.uniStormSystem;
                float len = GamePrefs.battledaylengthinmins / (SharedSpeed * rate);
                u.dayLength = len;
                u.nightLength = len;
            }
        }
    }
}
