using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GTLive.Net
{
    /// <summary>Small helpers for building and reading message payloads.</summary>
    public static class Wire
    {
        public static byte[] Build(Action<BinaryWriter> write)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                write(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Reader(byte[] data) => new BinaryReader(new MemoryStream(data), Encoding.UTF8);

        public static void Write(this BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        public static Vector3 ReadVector3(this BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static void WriteStr(this BinaryWriter w, string s) => w.Write(s ?? "");
    }
}
