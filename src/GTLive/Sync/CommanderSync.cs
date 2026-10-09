using System.Collections.Generic;
using System.IO;
using System.Text;
using GTLive.Net;
using UnityEngine;

namespace GTLive.Sync
{
    /// <summary>
    /// Units reference commanders by their index in GameVars.commander, so both lists must stay identical.
    /// The host watches a cheap signature of every commander and sends the full save record (Commander.Save)
    /// of any that changed or were added; the guest loads it in place with Commander.Load.
    /// </summary>
    public static class CommanderSync
    {
        // "major" = who the commander is and what he commands (sent at once); "minor" = attributes that drift daily
        private static readonly Dictionary<int, int> Major = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> Minor = new Dictionary<int, int>();
        private static float _next, _nextMinor;
        public static int Sent, Applied;

        public static void Reset()
        {
            Major.Clear();
            Minor.Clear();
            Sent = Applied = 0;
            _nextMinor = Time.unscaledTime + 30f;
            if (GameVars.commander == null) return;
            for (int i = 0; i < GameVars.commander.Count; i++)
            {
                Major[i] = MajorSig(GameVars.commander[i]);
                Minor[i] = MinorSig(GameVars.commander[i]);
            }
        }

        private static int MajorSig(GameVars.Commander c)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + c.rank;
                h = h * 31 + c.status;
                h = h * 31 + c.alliance;
                h = h * 31 + (c.usedummyphoto ? 1 : 0);
                h = h * 31 + (c.activeincampaign ? 1 : 0);
                h = h * 31 + (c.name?.GetHashCode() ?? 0);
                h = h * 31 + (c.currentcommand != null ? c.currentcommand.unitid : 0);
                return h;
            }
        }

        private static int MinorSig(GameVars.Commander c)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + c.battles;
                h = h * 31 + Mathf.RoundToInt(c.exp * 100f);
                h = h * 31 + Mathf.RoundToInt(c.fame * 100f);
                h = h * 31 + Mathf.RoundToInt(c.leadership * 100f);
                h = h * 31 + Mathf.RoundToInt(c.initiative * 100f);
                h = h * 31 + (c.servicehistory?.Length ?? 0);
                return h;
            }
        }

        /// <summary>Host: send changed commanders. Called before StructureSync so new units find their commander.</summary>
        public static void HostTick(bool force)
        {
            if (!force && Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 3f;
            bool minorPass = Time.unscaledTime >= _nextMinor;
            if (minorPass) _nextMinor = Time.unscaledTime + 30f;
            var list = GameVars.commander;
            var changed = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                int major = MajorSig(list[i]);
                bool majorChanged = !Major.TryGetValue(i, out int oldMajor) || oldMajor != major;
                bool minorChanged = false;
                if (minorPass)
                {
                    int minor = MinorSig(list[i]);
                    minorChanged = !Minor.TryGetValue(i, out int oldMinor) || oldMinor != minor;
                    Minor[i] = minor;
                }
                if (!majorChanged && !minorChanged) continue;
                Major[i] = major;
                if (!Minor.ContainsKey(i)) Minor[i] = MinorSig(list[i]);
                changed.Add(i);
            }
            if (changed.Count == 0) return;
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
            {
                foreach (int i in changed) list[i].Save(i, new StreamWriterShim(sw));
            }
            string text = sb.ToString();
            Session.Send(MsgType.Commanders, Wire.Build(w =>
            {
                w.Write(list.Count);
                w.Write(changed.Count);
                w.Write(MainMenuControls.currentversion);
                w.Write(text);
            }));
            Sent += changed.Count;
        }

        public static void OnCommanders(BinaryReader r)
        {
            int hostCount = r.ReadInt32();
            int n = r.ReadInt32();
            float version = r.ReadSingle();
            string text = r.ReadString();
            var list = GameVars.commander;
            var touched = new List<int>();
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            using (var sr = new StreamReader(ms, Encoding.UTF8))
            {
                for (int k = 0; k < n; k++)
                {
                    int id = int.Parse(sr.ReadLine());
                    while (list.Count <= id) list.Add(new GameVars.Commander());
                    list[id].Load(sr, version, resetexp: false);
                    touched.Add(id);
                    Applied++;
                }
            }
            // currentcommand isn't part of the record; rebuild it the way import_commanders does
            foreach (int id in touched)
            {
                Regiment cmd = null;
                foreach (var u in GameApi.Units())
                    if (u.commander == id && u.parentregiment == null && u.unittyp != 3 && u.unittyp != 4 && u.unittyp != 5) { cmd = u; break; }
                list[id].currentcommand = cmd;
            }
        }
    }

    /// <summary>Commander.Save wants a StreamWriter; this one writes into a StringWriter.</summary>
    internal sealed class StreamWriterShim : StreamWriter
    {
        private readonly StringWriter _inner;

        public StreamWriterShim(StringWriter inner) : base(Stream.Null) { _inner = inner; }

        public override void Write(char value) => _inner.Write(value);
        public override void Write(string value) => _inner.Write(value);
        public override void Write(char[] buffer, int index, int count) => _inner.Write(buffer, index, count);
        public override void WriteLine(string value) { _inner.Write(value); _inner.Write('\n'); }
        public override void WriteLine() => _inner.Write('\n');
        public override void WriteLine(int value) => WriteLine(value.ToString());
        public override void WriteLine(float value) => WriteLine(value.ToString());
        public override void WriteLine(bool value) => WriteLine(value.ToString());
        public override void WriteLine(object value) => WriteLine(value?.ToString() ?? "");
    }
}
