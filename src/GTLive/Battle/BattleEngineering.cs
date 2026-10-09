using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GTLive.Net;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// Engineering in a shared battle: pontoon bridges (pre-placed sites that engineers build) and obstacle lines
    /// (trenches and barricades a player draws). The host's game is the authority and sends both once a second
    /// when they change; the guest's own construction orders go to the host. Bridges matter beyond looks: an
    /// unfinished bridge is impassable to the pathfinder, so both games must agree on which ones are built.
    /// </summary>
    public static class BattleEngineering
    {
        private static readonly AccessTools.FieldRef<Pontoons, float[]> Finished = AccessTools.FieldRefAccess<Pontoons, float[]>("finished");
        private static readonly AccessTools.FieldRef<Pontoons, bool[]> Construct = AccessTools.FieldRefAccess<Pontoons, bool[]>("construct");
        private static readonly MethodInfo UpdateAlpha = AccessTools.Method(typeof(Pontoons), "UpdateAlphaForSegments");
        private static readonly MethodInfo FinishModel = AccessTools.Method(typeof(ObstacleLine), "FinishConstruction");
        private static readonly MethodInfo SetSubModels = AccessTools.Method(typeof(ObstacleLine), "SetActiveSubModels");

        public static int PontoonsSent, PontoonsApplied, ObstaclesSent, ObstaclesApplied, ObstacleRebuilds, GuestObstacleLines, GuestPontoonOrders;
        private static float _nextPontoons, _nextObstacles;
        private static string _pontoonSig = "", _obstacleSig = "";
        private static readonly HashSet<ObstacleLine> SentLines = new HashSet<ObstacleLine>();

        public static void Reset()
        {
            PontoonsSent = PontoonsApplied = ObstaclesSent = ObstaclesApplied = ObstacleRebuilds = GuestObstacleLines = GuestPontoonOrders = 0;
            _pontoonSig = _obstacleSig = "";
            _nextPontoons = _nextObstacles = 0f;
            SentLines.Clear();
        }

        // ---- pontoons -----------------------------------------------------------------------------------

        public static List<Pontoons> AllPontoons()
        {
            var list = new List<Pontoons>();
            var bu = GameApi.BU;
            if (bu?.pontoons == null) return list;
            foreach (var go in bu.pontoons) list.Add(go != null ? go.GetComponent<Pontoons>() : null);
            return list;
        }

        private static int NetOf(GameObject go) => go != null ? BattleSync.NetId(go.GetComponent<Regiment>()) : -1;

        private static GameObject UnitGo(int id)
        {
            var r = id >= 0 ? BattleSync.Find(id) : null;
            return r != null ? r.gameObject : null;
        }

        private static void WritePontoon(BinaryWriter w, int index, Pontoons p)
        {
            var f = Finished(p);
            w.Write((short)index);
            w.WriteStr(p.gameObject.name);
            w.Write(f[0]);
            w.Write(f[1]);
            w.Write(p.completed);
            w.Write(NetOf(p.allocatedreg[0]));
            w.Write(NetOf(p.allocatedreg[1]));
        }

        private struct PontoonState
        {
            public int Index, Alloc0, Alloc1;
            public string Name;
            public float F0, F1;
            public bool Completed;
        }

        private static PontoonState ReadPontoon(BinaryReader r) => new PontoonState
        {
            Index = r.ReadInt16(), Name = r.ReadString(), F0 = r.ReadSingle(), F1 = r.ReadSingle(),
            Completed = r.ReadBoolean(), Alloc0 = r.ReadInt32(), Alloc1 = r.ReadInt32(),
        };

        private static string PontoonSignature(List<Pontoons> all)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null) continue;
                var f = Finished(p);
                sb.Append(i).Append(':').Append(Mathf.RoundToInt(f[0] * 50)).Append('/').Append(Mathf.RoundToInt(f[1] * 50))
                  .Append(p.completed ? 'C' : 'o').Append(NetOf(p.allocatedreg[0])).Append(',').Append(NetOf(p.allocatedreg[1])).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Host: every pontoon site's progress, when anything changed (or forced).</summary>
        private static void BroadcastPontoons(bool force)
        {
            var all = AllPontoons();
            if (all.Count == 0) return;
            string sig = PontoonSignature(all);
            if (!force && sig == _pontoonSig) return;
            _pontoonSig = sig;
            BattleSync.SendEvent(BattleSync.EvPontoons, w =>
            {
                int n = 0;
                foreach (var p in all) if (p != null) n++;
                w.Write((short)n);
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null) WritePontoon(w, i, all[i]);
            });
            PontoonsSent++;
        }

        private static Pontoons Resolve(List<Pontoons> all, PontoonState s)
        {
            if (s.Index < 0 || s.Index >= all.Count) return null;
            var p = all[s.Index];
            return p != null && p.gameObject.name == s.Name ? p : null;
        }

        private static void Allocate(Pontoons p, int slot, GameObject unit)
        {
            if (p.allocatedreg[slot] == unit) return;
            var old = p.allocatedreg[slot] != null ? p.allocatedreg[slot].GetComponent<Regiment>() : null;
            if (old != null && old.pontoonconstruction == p.gameObject) old.pontoonconstruction = null;
            p.allocatedreg[slot] = unit;
            var reg = unit != null ? unit.GetComponent<Regiment>() : null;
            if (reg != null) reg.pontoonconstruction = p.gameObject;
        }

        private static void SetProgress(Pontoons p, float f0, float f1, bool completed)
        {
            var f = Finished(p);
            f[0] = f0;
            f[1] = f1;
            if (completed && !p.completed) p.SetCompleted(true);
            // show the built segments (the game only redraws them while someone is building)
            var c = Construct(p);
            bool c0 = c[0], c1 = c[1];
            c[0] = f0 > 0f;
            c[1] = f1 > 0f;
            try { UpdateAlpha.Invoke(p, null); }
            catch { }
            c[0] = c0;
            c[1] = c1;
        }

        /// <summary>Guest: the host's bridges.</summary>
        public static void OnPontoons(BinaryReader r)
        {
            var all = AllPontoons();
            int n = r.ReadInt16();
            Mp.Applying = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var s = ReadPontoon(r);
                    var p = Resolve(all, s);
                    if (p == null) continue;
                    Allocate(p, 0, UnitGo(s.Alloc0));
                    Allocate(p, 1, UnitGo(s.Alloc1));
                    SetProgress(p, s.F0, s.F1, s.Completed);
                }
            }
            finally { Mp.Applying = false; }
            PontoonsApplied++;
        }

        /// <summary>Guest: our player put engineers on a bridge (or finished one with deployment points).</summary>
        public static void SendGuestPontoon(Pontoons p)
        {
            var all = AllPontoons();
            int index = all.IndexOf(p);
            if (index < 0) return;
            BattleSync.SendEvent(BattleSync.EvPontoonOrder, w => WritePontoon(w, index, p));
            GuestPontoonOrders++;
        }

        /// <summary>Host: the guest's engineers take a bridge site; progress only ever goes up.</summary>
        public static void OnGuestPontoon(BinaryReader r)
        {
            var all = AllPontoons();
            var s = ReadPontoon(r);
            var p = Resolve(all, s);
            if (p == null || p.completed) return;
            Mp.Applying = true;
            try
            {
                int[] alloc = { s.Alloc0, s.Alloc1 };
                for (int k = 0; k < 2; k++)
                {
                    var unit = UnitGo(alloc[k]);
                    var reg = unit != null ? unit.GetComponent<Regiment>() : null;
                    if (reg == null || reg.alliance != Mp.GuestAlliance) continue;
                    if (p.allocatedreg[k] == null || p.allocatedreg[k] == unit) Allocate(p, k, unit);
                }
                var f = Finished(p);
                SetProgress(p, Mathf.Max(f[0], s.F0), Mathf.Max(f[1], s.F1), s.Completed || p.completed);
            }
            finally { Mp.Applying = false; }
            _nextPontoons = 0f;
        }

        // ---- obstacle lines ------------------------------------------------------------------------------

        private class ModelData
        {
            public Vector3 Pos, Rot;
            public float Completion;
            public int Level, Alloc;
        }

        private class LineData
        {
            public int Type;
            public readonly List<ModelData> Models = new List<ModelData>();
        }

        /// <summary>Finished drawing: every line except the one the local player is still drawing.</summary>
        private static List<ObstacleLine> FinishedLines()
        {
            var bui = GameApi.BUI;
            var list = new List<ObstacleLine>();
            if (bui?.obstaclelines == null) return list;
            int last = bui.obstaclebuilding >= 0 ? bui.obstaclelines.Count - 1 : bui.obstaclelines.Count;
            for (int i = 0; i < last; i++)
                if (bui.obstaclelines[i] != null) list.Add(bui.obstaclelines[i]);
            return list;
        }

        private static bool Placed(ObstacleLine.ModelObject m) => m != null && m.modelobj != null && m.state == 1 && m.modelobj.activeSelf;

        private static LineData Capture(ObstacleLine line)
        {
            var d = new LineData { Type = line.type };
            foreach (var m in line.modelobjects)
            {
                if (!Placed(m)) continue;
                d.Models.Add(new ModelData
                {
                    Pos = m.modelobj.transform.position, Rot = m.modelobj.transform.eulerAngles,
                    Completion = m.completion, Level = line.CountModelUpgradeSteps(m.modelobj), Alloc = NetOf(m.allocatedreg),
                });
            }
            return d;
        }

        private static void WriteLine(BinaryWriter w, LineData d)
        {
            w.Write((byte)d.Type);
            w.Write((short)d.Models.Count);
            foreach (var m in d.Models)
            {
                w.Write(m.Pos);
                w.Write(m.Rot);
                w.Write(m.Completion);
                w.Write((sbyte)m.Level);
                w.Write(m.Alloc);
            }
        }

        private static LineData ReadLine(BinaryReader r)
        {
            var d = new LineData { Type = r.ReadByte() };
            int n = r.ReadInt16();
            for (int i = 0; i < n; i++)
                d.Models.Add(new ModelData { Pos = r.ReadVector3(), Rot = r.ReadVector3(), Completion = r.ReadSingle(), Level = r.ReadSByte(), Alloc = r.ReadInt32() });
            return d;
        }

        /// <summary>Where the lines are (their shape); completion and workers can differ without a rebuild.</summary>
        private static string Shape(List<LineData> lines)
        {
            var sb = new StringBuilder();
            foreach (var d in lines)
            {
                sb.Append(d.Type).Append(':').Append(d.Models.Count);
                if (d.Models.Count > 0) sb.Append('@').Append(Mathf.RoundToInt(d.Models[0].Pos.x)).Append(',').Append(Mathf.RoundToInt(d.Models[0].Pos.z));
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static string Signature(List<LineData> lines)
        {
            var sb = new StringBuilder(Shape(lines));
            foreach (var d in lines)
                foreach (var m in d.Models)
                    sb.Append(Mathf.RoundToInt(m.Completion * 20)).Append(m.Level).Append(m.Alloc).Append('|');
            return sb.ToString();
        }

        private static void BroadcastObstacles(bool force)
        {
            var lines = new List<LineData>();
            foreach (var l in FinishedLines()) lines.Add(Capture(l));
            string sig = Signature(lines);
            if (!force && sig == _obstacleSig) return;
            _obstacleSig = sig;
            BattleSync.SendEvent(BattleSync.EvObstacles, w =>
            {
                w.Write((short)lines.Count);
                foreach (var d in lines) WriteLine(w, d);
            });
            ObstaclesSent++;
        }

        /// <summary>Builds a line the way the game's save loader does (ObstacleLine.Load), from our own data.</summary>
        private static ObstacleLine Build(LineData d)
        {
            var bui = GameApi.BUI;
            var line = bui.gameObject.AddComponent<ObstacleLine>();
            line.type = d.Type;
            float budget = 99999f;
            foreach (var m in d.Models)
            {
                line.modelobjects.Add(new ObstacleLine.ModelObject { segmentref = 0, state = 1 });
                ObstacleLine.PlaceObstacle(line, d.Type, m.Pos, m.Rot, ref budget, Mathf.Max(0, m.Level));
                line.modelobjects[line.modelobjects.Count - 1].completion = m.Completion;
                if (m.Completion >= 1f) FinishModel.Invoke(line, new object[] { line.modelobjects.Count - 1, true });
            }
            line.ResetColorForAllSegments();
            ApplyWorkers(line, d);
            bui.obstaclelines.Add(line);
            return line;
        }

        private static void ApplyWorkers(ObstacleLine line, LineData d)
        {
            int k = 0;
            foreach (var m in line.modelobjects)
            {
                if (!Placed(m)) continue;
                if (k >= d.Models.Count) break;
                var unit = UnitGo(d.Models[k].Alloc);
                if (m.allocatedreg != unit)
                {
                    var old = m.allocatedreg != null ? m.allocatedreg.GetComponent<Regiment>() : null;
                    if (old != null && old.obstacleconstruction == m.modelobj) old.obstacleconstruction = null;
                    m.allocatedreg = unit;
                    var reg = unit != null ? unit.GetComponent<Regiment>() : null;
                    if (reg != null) reg.obstacleconstruction = m.modelobj;
                }
                k++;
            }
        }

        private static void Destroy(ObstacleLine line)
        {
            foreach (var m in line.modelobjects)
            {
                if (m?.allocatedreg != null)
                {
                    var reg = m.allocatedreg.GetComponent<Regiment>();
                    if (reg != null && reg.obstacleconstruction == m.modelobj) reg.obstacleconstruction = null;
                }
                if (m?.modelobj != null) Object.Destroy(m.modelobj);
            }
            line.modelobjects.Clear();
            Object.Destroy(line);
        }

        /// <summary>Guest: the host's obstacle lines (which include the guest's own, once the host has them).</summary>
        public static void OnObstacles(BinaryReader r)
        {
            var bui = GameApi.BUI;
            if (bui?.obstaclelines == null) return;
            int n = r.ReadInt16();
            var host = new List<LineData>();
            for (int i = 0; i < n; i++) host.Add(ReadLine(r));

            var mine = FinishedLines();
            var local = new List<LineData>();
            foreach (var l in mine) local.Add(Capture(l));

            Mp.Applying = true;
            try
            {
                if (Shape(local) != Shape(host))
                {
                    // different lines: replace ours, keeping a line our player is still drawing
                    ObstacleLine drawing = bui.obstaclebuilding >= 0 && bui.obstaclelines.Count > 0 ? bui.obstaclelines[bui.obstaclelines.Count - 1] : null;
                    foreach (var l in mine) Destroy(l);
                    bui.obstaclelines.Clear();
                    SentLines.Clear();
                    foreach (var d in host) SentLines.Add(Build(d));
                    if (drawing != null) bui.obstaclelines.Add(drawing);
                    ObstacleRebuilds++;
                }
                else
                {
                    for (int i = 0; i < mine.Count; i++)
                    {
                        var line = mine[i];
                        var d = host[i];
                        int k = 0;
                        for (int j = 0; j < line.modelobjects.Count && k < d.Models.Count; j++)
                        {
                            var m = line.modelobjects[j];
                            if (!Placed(m)) continue;
                            var hm = d.Models[k++];
                            if (hm.Completion >= 1f && m.completion < 1f) FinishModel.Invoke(line, new object[] { j, false });
                            m.completion = hm.Completion;
                            if (hm.Level >= 0 && hm.Level != line.CountModelUpgradeSteps(m.modelobj))
                            {
                                m.level = hm.Level;
                                SetSubModels.Invoke(line, new object[] { j, true });
                            }
                        }
                        ApplyWorkers(line, d);
                        SentLines.Add(line);
                    }
                }
            }
            finally { Mp.Applying = false; }
            ObstaclesApplied++;
        }

        /// <summary>Guest: our player finished drawing a line (and maybe built it at once with deployment points).</summary>
        public static void SendNewGuestLines()
        {
            foreach (var line in FinishedLines())
            {
                if (SentLines.Contains(line)) continue;
                SentLines.Add(line);
                var d = Capture(line);
                if (d.Models.Count == 0) continue;
                BattleSync.SendEvent(BattleSync.EvObstacleNew, w => WriteLine(w, d));
                GuestObstacleLines++;
            }
        }

        /// <summary>Host: a line the guest drew.</summary>
        public static void OnGuestLine(BinaryReader r)
        {
            var d = ReadLine(r);
            if (d.Models.Count == 0 || GameApi.BUI == null) return;
            // only the guest's own units can work on the guest's line
            foreach (var m in d.Models)
            {
                var unit = UnitGo(m.Alloc);
                if (unit == null || unit.GetComponent<Regiment>().alliance != Mp.GuestAlliance) m.Alloc = -1;
            }
            Mp.Applying = true;
            try
            {
                var bui = GameApi.BUI;
                ObstacleLine drawing = bui.obstaclebuilding >= 0 && bui.obstaclelines.Count > 0 ? bui.obstaclelines[bui.obstaclelines.Count - 1] : null;
                if (drawing != null) bui.obstaclelines.Remove(drawing);
                Build(d);
                if (drawing != null) bui.obstaclelines.Add(drawing);
            }
            finally { Mp.Applying = false; }
            _nextObstacles = 0f;
        }

        // ---- per frame -------------------------------------------------------------------------------------

        public static void HostTick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextPontoons)
            {
                _nextPontoons = now + 1f;
                BroadcastPontoons(force: false);
            }
            if (now >= _nextObstacles)
            {
                _nextObstacles = now + 1f;
                BroadcastObstacles(force: false);
            }
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            var all = AllPontoons();
            int built = 0, building = 0;
            foreach (var p in all)
            {
                if (p == null) continue;
                if (p.completed) built++;
                else if (Finished(p)[0] + Finished(p)[1] > 0f || p.allocatedreg[0] != null || p.allocatedreg[1] != null) building++;
            }
            sb.Append($"pontoons={all.Count} built={built} building={building}");
            var lines = FinishedLines();
            int models = 0, done = 0;
            foreach (var l in lines)
                foreach (var m in l.modelobjects)
                    if (Placed(m)) { models++; if (m.completion >= 1f) done++; }
            sb.Append($" obstacleLines={lines.Count} segments={models} finished={done}");
            sb.Append($" sent p{PontoonsSent}/o{ObstaclesSent} applied p{PontoonsApplied}/o{ObstaclesApplied} rebuilds={ObstacleRebuilds} guestLines={GuestObstacleLines} guestPontoons={GuestPontoonOrders}");
            return sb.ToString();
        }

        public static string PontoonList()
        {
            var sb = new StringBuilder();
            var all = AllPontoons();
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null) { sb.AppendLine($"{i}: (none)"); continue; }
                var f = Finished(p);
                var lz = p.landingzone;
                string zones = lz != null && lz.Length == 2 ? $"lz0=({lz[0].x:F0},{lz[0].z:F0}) lz1=({lz[1].x:F0},{lz[1].z:F0})" : "";
                sb.AppendLine($"{i}: {p.gameObject.name} active={p.gameObject.activeInHierarchy} pos=({p.transform.position.x:F0},{p.transform.position.z:F0}) {zones} f={f[0]:F2}/{f[1]:F2} completed={p.completed} alloc={NetOf(p.allocatedreg[0])},{NetOf(p.allocatedreg[1])}");
            }
            return sb.ToString();
        }
    }
}
