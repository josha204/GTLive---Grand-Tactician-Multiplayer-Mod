using System;
using System.IO;
using System.IO.Compression;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// Economy and politics. The host periodically writes nations.dat (every state and alliance: treasury, debt,
    /// taxes, national morale, policies, weapon stock, recruits, support...) and the guest applies it with the
    /// game's own ImportExportUnitData.OverwriteNationsData. The guest's slider decisions for its own alliance
    /// travel the other way as a small Decisions message and are re-applied on the guest until the host has them.
    /// </summary>
    public static class EconomySync
    {
        public static float Interval = 4f;
        public static bool Applying;
        public static int Pushes, Applies;

        private static float _nextPush;
        private static int _pushSeq;

        // guest decisions
        private static Decisions _lastSent;
        private static int _decisionSeq;     // guest: last sent; host: last applied
        private static float _nextDecisionPoll;

        public static void Reset()
        {
            _nextPush = Time.unscaledTime + 2f;
            _lastSent = null;
            _decisionSeq = 0;
            Pushes = Applies = 0;
        }

        private static string TempDir => Path.Combine(Path.GetTempPath(), "gtlive_econ_" + (string.IsNullOrEmpty(Plugin.InstanceId) ? "main" : Plugin.InstanceId)) + Path.DirectorySeparatorChar;

        // ---- host -------------------------------------------------------------------------------------

        public static void HostTick()
        {
            if (Time.unscaledTime < _nextPush) return;
            _nextPush = Time.unscaledTime + Interval;
            Push();
        }

        public static void Push()
        {
            Directory.CreateDirectory(TempDir);
            ImportExportUnitData.SaveNationsData(TempDir);
            byte[] raw = File.ReadAllBytes(TempDir + "nations.dat");
            byte[] packed = Deflate(raw);
            int seq = ++_pushSeq;
            int ack = _decisionSeq;
            Session.Send(MsgType.Economy, Wire.Build(w =>
            {
                w.Write(seq);
                w.Write(ack);
                w.Write(MainMenuControls.currentversion);
                w.Write(packed.Length);
                w.Write(packed);
            }));
            Pushes++;
        }

        public static void OnDecisions(BinaryReader r)
        {
            int seq = r.ReadInt32();
            var d = Decisions.Read(r);
            if (seq <= _decisionSeq) return;
            _decisionSeq = seq;
            d.ApplyTo(Mp.GuestAlliance);
        }

        // ---- guest ------------------------------------------------------------------------------------

        public static void GuestTick()
        {
            if (Time.unscaledTime < _nextDecisionPoll) return;
            _nextDecisionPoll = Time.unscaledTime + 0.5f;
            var now = Decisions.Capture(Mp.GuestAlliance);
            if (_lastSent != null && _lastSent.Same(now)) return;
            bool first = _lastSent == null;
            _lastSent = now;
            if (first) return; // baseline right after load
            int seq = ++_decisionSeq;
            Session.Send(MsgType.Decisions, Wire.Build(w => { w.Write(seq); now.Write(w); }));
        }

        public static void OnEconomy(BinaryReader r)
        {
            int seq = r.ReadInt32();
            int ack = r.ReadInt32();
            float version = r.ReadSingle();
            int n = r.ReadInt32();
            byte[] raw = Inflate(r.ReadBytes(n));
            Directory.CreateDirectory(TempDir);
            File.WriteAllBytes(TempDir + "nations.dat", raw);
            File.WriteAllText(TempDir + "version.dat", version.ToString(System.Globalization.CultureInfo.CurrentCulture) + Environment.NewLine);

            var prefs = AutoPrefs.FromGame();
            var mine = Decisions.Capture(Mp.GuestAlliance);
            Applying = true;
            try
            {
                // OverwriteNationsData appends trade agreements to the existing lists
                foreach (var a in GameVars.alliance) a.tradeagreement?.Clear();
                ImportExportUnitData.OverwriteNationsData(TempDir);
            }
            finally
            {
                Applying = false;
                prefs.ApplyToGame();
            }
            // decisions the host hasn't seen yet stay as the guest set them
            if (ack < _decisionSeq) mine.ApplyTo(Mp.GuestAlliance);
            Applies++;
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Inflate(byte[] data)
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

    /// <summary>Settings a player changes for their own alliance with sliders (finances and logistics panels).</summary>
    public class Decisions
    {
        public float[] Tax, Subsidies, TransportTarget;

        public static Decisions Capture(int alliance)
        {
            var a = GameVars.alliance[alliance];
            return new Decisions
            {
                Tax = (float[])a.tax.Clone(),
                Subsidies = (float[])a.subsidies.Clone(),
                TransportTarget = (float[])a.transportcapacitytarget.Clone(),
            };
        }

        public void ApplyTo(int alliance)
        {
            var a = GameVars.alliance[alliance];
            Copy(Tax, a.tax);
            Copy(Subsidies, a.subsidies);
            Copy(TransportTarget, a.transportcapacitytarget);
        }

        private static void Copy(float[] from, float[] to)
        {
            if (from == null || to == null) return;
            Array.Copy(from, to, Math.Min(from.Length, to.Length));
        }

        public bool Same(Decisions o) => Eq(Tax, o.Tax) && Eq(Subsidies, o.Subsidies) && Eq(TransportTarget, o.TransportTarget);

        private static bool Eq(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        public void Write(BinaryWriter w)
        {
            WriteArr(w, Tax);
            WriteArr(w, Subsidies);
            WriteArr(w, TransportTarget);
        }

        public static Decisions Read(BinaryReader r) => new Decisions { Tax = ReadArr(r), Subsidies = ReadArr(r), TransportTarget = ReadArr(r) };

        private static void WriteArr(BinaryWriter w, float[] a)
        {
            w.Write((byte)a.Length);
            foreach (var v in a) w.Write(v);
        }

        private static float[] ReadArr(BinaryReader r)
        {
            var a = new float[r.ReadByte()];
            for (int i = 0; i < a.Length; i++) a[i] = r.ReadSingle();
            return a;
        }
    }

    /// <summary>Re-setting an unchanged capital rebuilds every telegraph line; skip that during economy sync.</summary>
    [HarmonyPatch(typeof(GameVars.Alliance), nameof(GameVars.Alliance.SetCapital))]
    internal static class SameCapitalNoRebuild
    {
        private static bool Prefix(GameVars.Alliance __instance, Town newcapital) =>
            !(EconomySync.Applying && newcapital != null && __instance.capital == newcapital);
    }
}
