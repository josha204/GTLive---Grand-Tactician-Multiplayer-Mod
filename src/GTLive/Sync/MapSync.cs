using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// Weather is rolled randomly on each machine and drives movement speed and attrition, so the host's climate
    /// grid (clouds/precipitation/etc, temperature, wind and snow cover) is copied to the guest every ten seconds.
    /// </summary>
    public static class WeatherSync
    {
        private static readonly FieldInfo FClimate = AccessTools.Field(typeof(Weather), "climatedata");
        private static readonly FieldInfo FTemp = AccessTools.Field(typeof(Weather), "currenttemperature");
        private static readonly FieldInfo FWind = AccessTools.Field(typeof(Weather), "winddirection");
        private static readonly FieldInfo FSnowTex = AccessTools.Field(typeof(Weather), "snowtexture");
        private static readonly MethodInfo MSetSnow = AccessTools.Method(typeof(Weather), "SetSnowTexture");
        private static Weather _weather;
        private static float _next;
        public static int Pushes, Applies;

        private static Weather W
        {
            get
            {
                if (_weather == null)
                {
                    var go = GameObject.Find("WeatherObj");
                    if (go != null) _weather = go.GetComponent<Weather>();
                }
                return _weather;
            }
        }

        public static void Reset()
        {
            _weather = null;
            _next = Time.unscaledTime + 5f;
            Pushes = Applies = 0;
        }

        public static void HostTick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 10f;
            var w = W;
            if (w == null) return;
            var climate = FClimate.GetValue(w) as float[,];
            var temp = FTemp.GetValue(w) as float[];
            var wind = FWind.GetValue(w) as float[];
            var snow = w.currentsnowvalues;
            if (climate == null || temp == null) return;
            byte[] raw;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                int rows = climate.GetLength(0), n = climate.GetLength(1);
                bw.Write(rows); bw.Write(n);
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < n; j++) bw.Write(climate[i, j]);
                WriteArr(bw, temp);
                WriteArr(bw, wind);
                bw.Write(snow?.Length ?? 0);
                if (snow != null)
                    foreach (var c in snow) { bw.Write(c.r); bw.Write(c.g); bw.Write(c.b); bw.Write(c.a); }
                bw.Flush();
                raw = ms.ToArray();
            }
            var packed = Deflate(raw);
            Session.Send(MsgType.Weather, packed);
            Pushes++;
        }

        public static void OnWeather(byte[] packed)
        {
            var w = W;
            if (w == null) return;
            using (var br = new BinaryReader(new MemoryStream(Inflate(packed))))
            {
                int rows = br.ReadInt32(), n = br.ReadInt32();
                var climate = FClimate.GetValue(w) as float[,];
                if (climate == null || climate.GetLength(0) != rows || climate.GetLength(1) != n) climate = new float[rows, n];
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < n; j++) climate[i, j] = br.ReadSingle();
                FClimate.SetValue(w, climate);
                var temp = ReadArr(br);
                if (temp != null) FTemp.SetValue(w, temp);
                var wind = ReadArr(br);
                if (wind != null) FWind.SetValue(w, wind);
                int sn = br.ReadInt32();
                if (sn > 0)
                {
                    var snow = w.currentsnowvalues;
                    if (snow == null || snow.Length != sn) snow = new Color32[sn];
                    for (int i = 0; i < sn; i++) snow[i] = new Color32(br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadByte());
                    w.currentsnowvalues = snow;
                    if (FSnowTex.GetValue(w) is Texture2D tex && tex.width * tex.height == sn)
                    {
                        tex.SetPixels32(snow);
                        tex.Apply();
                        MSetSnow?.Invoke(w, null);
                    }
                }
            }
            Applies++;
        }

        private static void WriteArr(BinaryWriter bw, float[] a)
        {
            bw.Write(a?.Length ?? 0);
            if (a != null) foreach (var v in a) bw.Write(v);
        }

        private static float[] ReadArr(BinaryReader br)
        {
            int n = br.ReadInt32();
            if (n == 0) return null;
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = br.ReadSingle();
            return a;
        }

        public static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        public static byte[] Inflate(byte[] data)
        {
            using (var src = new MemoryStream(data))
            using (var z = new DeflateStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                z.CopyTo(dst);
                return dst.ToArray();
            }
        }
    }

    /// <summary>
    /// Battle monuments (the battle markers and their result panels). Engagements only run on the host, so the guest
    /// gets copies of the host's monuments through BattleMonument.Save/Load, refreshed while a battle is ongoing.
    /// </summary>
    public static class MonumentSync
    {
        private static readonly Dictionary<string, int> Sent = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> LastSentAt = new Dictionary<string, float>();
        private static float _next;
        public static int Updates;

        private static BattlefieldSetup Bfs => GameApi.BU != null ? GameApi.BU.GetComponent<BattlefieldSetup>() : null;

        public static string Key(BattleMonument m) =>
            m.battlename + "|" + (m.datefrom != null ? m.datefrom.yearfraction.ToString("F5") : "") + "|" + Mathf.RoundToInt(m.position.x) + "," + Mathf.RoundToInt(m.position.z);

        public static void Reset()
        {
            Sent.Clear();
            LastSentAt.Clear();
            Updates = 0;
            var bfs = Bfs;
            if (bfs?.battlemonument == null) return;
            foreach (var m in bfs.battlemonument)
                if (m != null) Sent[Key(m)] = Record(m).GetHashCode();
        }

        private static string Record(BattleMonument m)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb)) m.Save(new StreamWriterShim(sw));
            return sb.ToString();
        }

        public static void HostTick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 3f;
            var bfs = Bfs;
            if (bfs?.battlemonument == null) return;
            var records = new List<KeyValuePair<string, string>>();
            var present = new HashSet<string>();
            foreach (var m in bfs.battlemonument)
            {
                if (m == null) continue;
                string k = Key(m);
                present.Add(k);
                string rec = Record(m);
                int h = rec.GetHashCode();
                if (Sent.TryGetValue(k, out int old) && old == h) continue;
                // ongoing battles change every cycle; refresh their copy at most every ten seconds
                if (Sent.ContainsKey(k) && LastSentAt.TryGetValue(k, out float at) && Time.unscaledTime - at < 10f) continue;
                Sent[k] = h;
                LastSentAt[k] = Time.unscaledTime;
                records.Add(new KeyValuePair<string, string>(k, rec));
            }
            var removed = Sent.Keys.Where(k => !present.Contains(k)).ToList();
            foreach (var k in removed) { Sent.Remove(k); LastSentAt.Remove(k); }
            if (records.Count == 0 && removed.Count == 0) return;
            Session.Send(MsgType.Monuments, Wire.Build(w =>
            {
                w.Write(MainMenuControls.currentversion);
                w.Write(records.Count);
                foreach (var kv in records) { w.WriteStr(kv.Key); w.WriteStr(kv.Value); }
                w.Write(removed.Count);
                foreach (var k in removed) w.WriteStr(k);
            }));
            Updates += records.Count + removed.Count;
        }

        // the monument shown in an open battle prompt must not be replaced underneath the panel
        private static string _protected;

        public static void Unprotect() => _protected = null;

        public static void OnMonuments(BinaryReader r)
        {
            var bfs = Bfs;
            if (bfs == null) return;
            if (bfs.battlemonument == null) bfs.battlemonument = new List<BattleMonument>();
            float version = r.ReadSingle();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString(), rec = r.ReadString();
                if (key == _protected) continue;
                var existing = bfs.battlemonument.FirstOrDefault(m => m != null && Key(m) == key);
                if (existing != null && existing.autocalcref == null) existing.Remove();
                else if (existing != null) continue; // never replace a monument the guest is running itself
                Create(bfs, rec, version);
                Updates++;
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString();
                if (key == _protected) continue;
                var existing = bfs.battlemonument.FirstOrDefault(m => m != null && Key(m) == key);
                if (existing != null && existing.autocalcref == null) existing.Remove();
            }
            bfs.UpdateBattleMonumentVisibility();
        }

        private static BattleMonument Create(BattlefieldSetup bfs, string rec, float version)
        {
            using (var sr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(rec)), Encoding.UTF8))
            {
                int type = int.Parse(sr.ReadLine());
                var go = UnityEngine.Object.Instantiate(GameApi.BU.preloadedmodels.Monuments[type]);
                var m = go.GetComponent<BattleMonument>();
                bfs.battlemonument.Add(m);
                m.Add("", default, null, null, -1, -1, 0, 0, -1, -1, null, -1, -1, 0, 0, null, null, checkfordoublename: false);
                m.Load(sr, "", version);
                m.landorsea = type;
                return m;
            }
        }

        /// <summary>Guest: the monument of a battle prompt (created from the host's record if it isn't here yet), protected until resolved.</summary>
        public static BattleMonument EnsureMonument(string key, string rec, float version)
        {
            var bfs = Bfs;
            if (bfs == null) return null;
            if (bfs.battlemonument == null) bfs.battlemonument = new List<BattleMonument>();
            _protected = key;
            var existing = bfs.battlemonument.FirstOrDefault(m => m != null && Key(m) == key);
            if (existing != null) return existing;
            var created = Create(bfs, rec, version);
            bfs.UpdateBattleMonumentVisibility();
            return created;
        }
    }
}
