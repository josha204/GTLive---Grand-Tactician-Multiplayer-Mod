using System.Collections.Generic;
using System.IO;
using GTLive.Net;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// Host -> guest unit state.
    ///  - UnitState (1 Hz): every campaign formation's position and battle/retreat flags. The guest measures drift,
    ///    copies the flags and, when enabled, pulls stray formations back into place.
    ///  - Strength (every 2 s, changes only): men, sick, wounded/captured losses, morale and experience of every
    ///    unit, the same fields OverwriteUnits restores after a battle.
    /// </summary>
    public static class UnitStates
    {
        public static bool CorrectPositions = true;
        public static float CorrectThreshold = 3f;

        private static float _nextSend, _nextStrength;
        public static int Count, Missing, Corrected, StrengthUpdates;
        public static float AvgDrift, MaxDrift;
        public static string MaxDriftUnit = "";

        private struct Str
        {
            public int Strength, Sick, Wounded, Captured;
            public float Morale, Experience;

            public bool Same(Str o) => Strength == o.Strength && Sick == o.Sick && Wounded == o.Wounded && Captured == o.Captured
                                       && Mathf.Abs(Morale - o.Morale) < 0.005f && Mathf.Abs(Experience - o.Experience) < 0.005f;
        }

        private static readonly Dictionary<int, Str> SentStrength = new Dictionary<int, Str>();

        public static void Reset()
        {
            Count = Missing = Corrected = StrengthUpdates = 0;
            AvgDrift = MaxDrift = 0f;
            MaxDriftUnit = "";
            SentStrength.Clear();
            _nextStrength = Time.unscaledTime + 1f;
        }

        public static string Summary() =>
            $"n={Count} missing={Missing} extra={Extra} drift avg={AvgDrift:F2} max={MaxDrift:F2} ({MaxDriftUnit}) corrected={Corrected} strengthUpdates={StrengthUpdates}";

        // ---- host --------------------------------------------------------------------------------------

        public static void HostTick()
        {
            if (Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + 1f;
                SendPositions();
            }
            if (Time.unscaledTime >= _nextStrength)
            {
                _nextStrength = Time.unscaledTime + 2f;
                SendStrength();
            }
        }

        private static void SendPositions()
        {
            var list = new List<Regiment>(Orders.CampaignUnits());
            Session.Send(MsgType.UnitState, Wire.Build(w =>
            {
                w.Write(list.Count);
                foreach (var r in list)
                {
                    var p = r.transform.position;
                    w.Write(r.unitid);
                    w.Write(p.x);
                    w.Write(p.z);
                    byte f = (byte)((r.inbattle ? 1 : 0) | (r.onretreat ? 2 : 0) | (r.regimentpaths > 0 ? 4 : 0) | (r.lastretreatiswithdrawal ? 8 : 0));
                    w.Write(f);
                }
            }));
        }

        private static void SendStrength()
        {
            var changed = new List<KeyValuePair<int, Str>>();
            foreach (var r in GameApi.Units())
            {
                if (r.losses == null) continue;
                var s = new Str
                {
                    Strength = r.strength,
                    Sick = r.sick,
                    Wounded = r.losses.Length > 0 ? r.losses[0] : 0,
                    Captured = r.losses.Length > 3 ? r.losses[3] : 0,
                    Morale = r.morale,
                    Experience = r.experience,
                };
                if (SentStrength.TryGetValue(r.unitid, out var old) && old.Same(s)) continue;
                SentStrength[r.unitid] = s;
                changed.Add(new KeyValuePair<int, Str>(r.unitid, s));
            }
            if (changed.Count == 0) return;
            Session.Send(MsgType.Strength, Wire.Build(w =>
            {
                w.Write(changed.Count);
                foreach (var kv in changed)
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value.Strength);
                    w.Write(kv.Value.Sick);
                    w.Write(kv.Value.Wounded);
                    w.Write(kv.Value.Captured);
                    w.Write(kv.Value.Morale);
                    w.Write(kv.Value.Experience);
                }
            }));
        }

        // ---- guest -------------------------------------------------------------------------------------

        public static void OnState(BinaryReader rd)
        {
            int n = rd.ReadInt32();
            float sum = 0f, max = 0f;
            string maxName = "";
            int missing = 0;
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int id = rd.ReadInt32();
                float x = rd.ReadSingle(), z = rd.ReadSingle();
                byte f = rd.ReadByte();
                seen.Add(id);
                var r = GameApi.FindUnit(id);
                if (r == null) { missing++; continue; }
                r.inbattle = (f & 1) != 0;
                r.onretreat = (f & 2) != 0;
                r.lastretreatiswithdrawal = (f & 8) != 0;
                var p = r.transform.position;
                float d = Mathf.Sqrt((p.x - x) * (p.x - x) + (p.z - z) * (p.z - z));
                sum += d;
                if (d > max) { max = d; maxName = r.name; }
                if (CorrectPositions && d > CorrectThreshold)
                {
                    Mp.Applying = true;
                    try
                    {
                        var snap = OrderSnap.Capture(r);
                        r.SetNewPosition(new Vector3(x, p.y, z), r.transform.rotation.eulerAngles.y, usecoverplacement: false, ignoredeploymentzone: true);
                        if (snap.Waypoints.Count > 0) snap.ApplyTo(r);
                        Corrected++;
                    }
                    finally { Mp.Applying = false; }
                }
            }
            Count = n;
            Missing = missing;
            AvgDrift = n > missing ? sum / (n - missing) : 0f;
            MaxDrift = max;
            MaxDriftUnit = maxName;
            CheckStructure(seen);
        }

        // ---- desync safety net ------------------------------------------------------------------------

        public static bool AutoResync = true;
        public static int Extra;
        private static int _mismatchStreak;
        private static float _lastAutoResync = -999f;

        /// <summary>
        /// If the guest's set of campaign formations disagrees with the host's for several seconds in a row
        /// (something the live channels didn't cover), ask the host for a full resync.
        /// </summary>
        private static void CheckStructure(HashSet<int> hostIds)
        {
            int extra = 0;
            foreach (var r in Orders.CampaignUnits())
                if (!hostIds.Contains(r.unitid)) extra++;
            Extra = extra;
            if (Missing == 0 && extra == 0) { _mismatchStreak = 0; return; }
            _mismatchStreak++;
            if (!AutoResync || _mismatchStreak < 5 || Time.unscaledTime - _lastAutoResync < 60f) return;
            _lastAutoResync = Time.unscaledTime;
            _mismatchStreak = 0;
            Session.RequestResync($"auto: {Missing} formations missing, {extra} extra");
        }

        public static void OnStrength(BinaryReader rd)
        {
            int n = rd.ReadInt32();
            var touched = new HashSet<Regiment>();
            for (int i = 0; i < n; i++)
            {
                int id = rd.ReadInt32();
                int strength = rd.ReadInt32(), sick = rd.ReadInt32(), wounded = rd.ReadInt32(), captured = rd.ReadInt32();
                float morale = rd.ReadSingle(), exp = rd.ReadSingle();
                var r = GameApi.FindUnit(id);
                if (r == null) continue;
                r.strength = strength;
                r.sick = sick;
                if (r.losses != null && r.losses.Length > 3)
                {
                    r.losses[0] = wounded;
                    r.losses[3] = captured;
                }
                r.morale = morale;
                r.experience = exp;
                StrengthUpdates++;
                // refresh every formation above this unit (division, corps, army all show totals)
                for (var t = r.transform.parent; t != null; t = t.parent)
                {
                    var pr = t.GetComponent<Regiment>();
                    if (pr == null) break;
                    touched.Add(pr);
                }
            }
            foreach (var t in touched) t.UpdateGroupStrength(overridecalc: true, ignoreactivestate: true);
        }
    }
}
