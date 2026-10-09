using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GTLive.Net
{
    public enum MsgType : byte
    {
        Hello = 1,        // guest -> host: protocol, mod version, player name
        Welcome = 2,      // host -> guest: alliances, level, settings
        Reject = 3,       // host -> guest: reason
        SaveBegin = 4,    // host -> guest: level, sub, reason, total bytes
        SaveChunk = 5,
        SaveEnd = 6,
        Ready = 7,        // either -> other: campaign loaded and paused
        Go = 8,           // host -> guest: both ready, session is live
        Clock = 9,        // host -> guest: date, speed, paused
        SpeedRequest = 10,// guest -> host
        OrderRequest = 11,// guest -> host: order state for one of the guest's units
        OrderState = 12,  // host -> guest: authoritative order state for one unit
        UnitState = 13,   // host -> guest: batch of unit positions / strength / flags
        ResyncRequest = 14,
        Ping = 15,
        Pong = 16,
        Chat = 17,
        Notice = 18,      // host -> guest: text shown in the overlay (battles etc.)
        Bye = 19,
        Prefs = 20,       // either -> other: automanage flags
        Hash = 21,        // host -> guest: state checksum for desync detection
        Economy = 22,     // host -> guest: nations.dat (deflated)
        Decisions = 23,   // guest -> host: the guest alliance's slider settings
        Strength = 24,    // host -> guest: per-unit men / sick / losses / morale changes
        Commanders = 25,  // host -> guest: changed/new commander save records
        UnitCreate = 26,  // host -> guest: new units (full records)
        UnitDelete = 27,  // host -> guest: units that no longer exist
        UnitReparent = 28,// host -> guest: OOB changes
        StructureRequest = 29, // guest -> host: recruit / new group / OOB action for the guest's alliance
        World = 30,       // host -> guest: town / industry ownership, campaign buildings
        GuestAction = 31, // guest -> host: research, projects, weapons, commanders, construction
        Weather = 32,     // host -> guest: climate grid, temperature, wind, snow (deflated)
        Monuments = 33,   // host -> guest: battle monument records
        Vessels = 34,     // host -> guest: ships created / changed / gone
        VesselRequest = 35, // guest -> host: build a ship or move one between fleet and pool
        BattlePrompt = 36,  // host -> guest: battle panel contents
        BattleChoice = 37,  // guest -> host: the guest's battle order + participating formations
        BattleResolved = 38,// host -> guest: what happens
        BattleTransferBegin = 39, // host -> guest: battle save (campaign + Battle folder), then chunks and end
        BattleTransferChunk = 40,
        BattleTransferEnd = 41,
        BattleReady = 42,   // guest -> host: battle scene loaded
        BattleGo = 43,      // host -> guest: both on the field
        BattleState = 44,   // host -> guest: regiment states
        BattleOrder = 45,   // guest -> host: order for one of the guest's regiments
        BattleOrderState = 46, // host -> guest: authoritative order state
        BattleEvent = 47,   // either way: control changes, army orders, end of battle
        BattleFight = 48,   // guest -> host: "Fight" pressed on an engagement being autoresolved
    }

    public struct Packet
    {
        public MsgType Type;
        public byte[] Data;
    }

    /// <summary>One TCP connection. Frames are [int32 length][byte type][payload]. Threads only touch queues.</summary>
    public sealed class Link : IDisposable
    {
        private const int MaxFrame = 64 * 1024 * 1024;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly BlockingCollection<byte[]> _send = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        private readonly ConcurrentQueue<Packet> _recv = new ConcurrentQueue<Packet>();
        private readonly Thread _reader, _writer;
        private volatile bool _closed;

        public string CloseReason { get; private set; }
        public bool Closed => _closed;
        public string Remote { get; }
        public long BytesIn, BytesOut;
        public DateTime LastReceived = DateTime.UtcNow;

        public Link(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _client.ReceiveBufferSize = 1 << 20;
            _client.SendBufferSize = 1 << 20;
            _stream = client.GetStream();
            Remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "GTLive-read" };
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "GTLive-write" };
            _reader.Start();
            _writer.Start();
        }

        public void Send(MsgType type, byte[] payload)
        {
            if (_closed) return;
            payload = payload ?? new byte[0];
            var frame = new byte[5 + payload.Length];
            int len = payload.Length + 1;
            frame[0] = (byte)len;
            frame[1] = (byte)(len >> 8);
            frame[2] = (byte)(len >> 16);
            frame[3] = (byte)(len >> 24);
            frame[4] = (byte)type;
            Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
            _send.Add(frame);
        }

        public int PendingSends => _send.Count;

        public bool TryReceive(out Packet p) => _recv.TryDequeue(out p);

        private void ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (!_closed)
                {
                    ReadExact(header, 4);
                    int len = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                    if (len < 1 || len > MaxFrame) throw new IOException("bad frame length " + len);
                    var body = new byte[len];
                    ReadExact(body, len);
                    var data = new byte[len - 1];
                    Buffer.BlockCopy(body, 1, data, 0, len - 1);
                    Interlocked.Add(ref BytesIn, len + 4);
                    LastReceived = DateTime.UtcNow;
                    _recv.Enqueue(new Packet { Type = (MsgType)body[0], Data = data });
                }
            }
            catch (Exception e)
            {
                Close("read: " + e.Message);
            }
        }

        private void ReadExact(byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = _stream.Read(buf, off, count - off);
                if (n <= 0) throw new IOException("connection closed by peer");
                off += n;
            }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var frame in _send.GetConsumingEnumerable())
                {
                    if (_closed) break;
                    _stream.Write(frame, 0, frame.Length);
                    Interlocked.Add(ref BytesOut, frame.Length);
                }
            }
            catch (Exception e)
            {
                Close("write: " + e.Message);
            }
        }

        public void Close(string reason)
        {
            if (_closed) return;
            _closed = true;
            CloseReason = reason;
            try { _send.CompleteAdding(); } catch { }
            try { _stream.Close(); } catch { }
            try { _client.Close(); } catch { }
        }

        public void Dispose() => Close("disposed");

        /// <summary>Flushes queued frames (best effort, used before closing with a Bye).</summary>
        public void Drain(int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (_send.Count > 0 && DateTime.UtcNow < until && !_closed) Thread.Sleep(5);
        }
    }

    /// <summary>Accepts exactly one guest at a time on a background thread.</summary>
    public sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly ConcurrentQueue<TcpClient> _accepted = new ConcurrentQueue<TcpClient>();
        private volatile bool _stopped;
        public string Error;

        public Listener(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "GTLive-accept" };
            t.Start();
        }

        private void AcceptLoop()
        {
            try
            {
                while (!_stopped)
                {
                    var c = _listener.AcceptTcpClient();
                    _accepted.Enqueue(c);
                }
            }
            catch (Exception e)
            {
                if (!_stopped) Error = e.Message;
            }
        }

        public bool TryAccept(out TcpClient c) => _accepted.TryDequeue(out c);

        public void Dispose()
        {
            _stopped = true;
            try { _listener.Stop(); } catch { }
        }
    }

    /// <summary>Connects on a background thread so the game never stalls on DNS or a dead host.</summary>
    public sealed class Connector
    {
        public volatile TcpClient Result;
        public volatile string Error;
        public volatile bool Done;

        public Connector(string host, int port)
        {
            var t = new Thread(() =>
            {
                try
                {
                    var c = new TcpClient();
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    {
                        c.Close();
                        throw new TimeoutException("no answer from " + host + ":" + port);
                    }
                    c.EndConnect(ar);
                    Result = c;
                }
                catch (Exception e)
                {
                    Error = e.Message;
                }
                finally
                {
                    Done = true;
                }
            }) { IsBackground = true, Name = "GTLive-connect" };
            t.Start();
        }
    }
}
