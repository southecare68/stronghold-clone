// Wire.cs — Turn serialization. Engine-agnostic on purpose: this is protocol,
// not rendering, and both a Godot client and a plain `dotnet` test must produce
// byte-identical packets from the same turn.
//
// Everything is written as explicit little-endian bytes rather than through
// BitConverter. BitConverter follows the host's endianness, and this project's
// entire premise is that an ARM Mac and an x86 Linux box agree exactly — a
// format that silently depends on the host is precisely the class of bug the
// fixed-point rule exists to eliminate. Being explicit costs a few lines once.
//
// Integers are fixed-width; no varints, no compression. A turn is tens of bytes.

using System;
using System.Collections.Generic;
using Sim;

namespace Netcode
{
    public static class Wire
    {
        // 'S','H' then a format version. Bump the version if the layout changes;
        // a peer running an older build will be rejected instead of quietly
        // misreading a packet and desyncing on tick 1.
        const byte Magic0 = (byte)'S';
        const byte Magic1 = (byte)'H';
        public const byte Version = 1;

        // The fourth header byte says what the packet is. It was reserved for
        // exactly this; turns keep the value 0, so their layout is unchanged.
        public enum Kind : byte { Turn = 0, Snapshot = 1 }

        // A malformed or hostile packet must not be able to make us allocate an
        // arbitrary amount. No legitimate message comes anywhere near these.
        const int MaxCommands = 4096;
        const int MaxUnitsPerCommand = 4096;
        const int MaxUnits = 65536;
        const int MaxPendingTurns = 1024;

        // What kind of message is this, if any? Returns null for anything we
        // cannot identify, so callers can drop it without parsing further.
        public static Kind? KindOf(byte[] data)
        {
            if (data == null || data.Length < 4) return null;
            if (data[0] != Magic0 || data[1] != Magic1 || data[2] != Version) return null;
            if (data[3] != (byte)Kind.Turn && data[3] != (byte)Kind.Snapshot) return null;
            return (Kind)data[3];
        }

        public static byte[] Serialize(TurnInput turn)
        {
            var buf = new List<byte>(64);
            WriteHeader(buf, Kind.Turn);
            WriteTurn(buf, turn);
            return buf.ToArray();
        }

        static void WriteHeader(List<byte> buf, Kind kind)
        {
            buf.Add(Magic0);
            buf.Add(Magic1);
            buf.Add(Version);
            buf.Add((byte)kind);             // keeps the header 4-aligned
        }

        static void WriteTurn(List<byte> buf, TurnInput turn)
        {
            PutInt(buf, turn.Owner);
            PutInt(buf, turn.Tick);
            PutInt(buf, turn.ChecksumTick);
            PutUInt(buf, turn.Checksum);

            PutInt(buf, turn.Commands.Length);
            foreach (var c in turn.Commands)
            {
                PutInt(buf, c.Owner);
                PutInt(buf, (int)c.Type);
                PutInt(buf, c.X);
                PutInt(buf, c.Y);
                PutInt(buf, c.TargetId);      // Attack orders carry their target
                PutInt(buf, c.ExecTick);
                PutInt(buf, c.Seq);          // dropping this reintroduces the
                                             // ordering desync — see tests/CommandOrder
                PutInt(buf, c.UnitIds.Length);
                foreach (int id in c.UnitIds) PutInt(buf, id);
            }
        }

        public static byte[] Serialize(MatchSnapshot snap)
        {
            var buf = new List<byte>(256);
            WriteHeader(buf, Kind.Snapshot);

            PutInt(buf, snap.Tick);
            PutInt(buf, snap.NextUnitId);
            PutInt(buf, snap.NextNodeId);
            PutUInt(buf, snap.RngState);
            PutUInt(buf, snap.Checksum);

            PutInt(buf, snap.Units.Length);
            foreach (var u in snap.Units)
            {
                PutInt(buf, u.Id);
                PutInt(buf, u.Owner);
                PutInt(buf, u.X);
                PutInt(buf, u.Y);
                PutInt(buf, u.Tx);    // the target travels too — a unit restored
                PutInt(buf, u.Ty);    // without it would stop dead on arrival
                PutInt(buf, u.Hp);
                PutInt(buf, u.MaxHp);
                PutInt(buf, u.TargetId);
                PutInt(buf, u.AttackTimer);
                PutInt(buf, (int)u.Job);
                PutInt(buf, u.GatherNodeId);
                PutInt(buf, (int)u.CarryType);
                PutInt(buf, u.CarryAmount);
                PutInt(buf, u.GatherTimer);

                // The remaining route. StateChecksum hashes it, so a snapshot that
                // dropped it would fail its own checksum verification on arrival —
                // the rejoiner would rebuild a unit that had lost its orders.
                int remaining = u.HasPath ? u.Path.Count - u.PathIndex : 0;
                PutInt(buf, remaining);
                for (int i = u.PathIndex; i < remaining + u.PathIndex; i++)
                {
                    PutInt(buf, u.Path[i].X);
                    PutInt(buf, u.Path[i].Y);
                }
            }

            PutInt(buf, snap.Nodes.Length);
            foreach (var n in snap.Nodes)
            {
                PutInt(buf, n.Id);
                PutInt(buf, (int)n.Type);
                PutInt(buf, n.X);
                PutInt(buf, n.Y);
                PutInt(buf, n.Amount);
            }

            // Stockpiles and drop-offs, each written in the snapshot's iteration
            // order (a SortedDictionary in the sim, so owner order everywhere).
            PutInt(buf, snap.Stock.Count);
            foreach (var kv in snap.Stock)
            {
                PutInt(buf, kv.Key);
                PutInt(buf, kv.Value.Length);
                foreach (int amt in kv.Value) PutInt(buf, amt);
            }
            PutInt(buf, snap.DropOffs.Count);
            foreach (var kv in snap.DropOffs)
            {
                PutInt(buf, kv.Key);
                PutInt(buf, kv.Value.X);
                PutInt(buf, kv.Value.Y);
            }

            PutInt(buf, snap.PendingTurns.Length);
            foreach (var turn in snap.PendingTurns) WriteTurn(buf, turn);

            return buf.ToArray();
        }

        // Returns null for anything we cannot fully and safely read. Callers treat
        // null as "drop the packet"; a lockstep client that acts on a half-read
        // message is worse off than one that keeps waiting.
        public static TurnInput Deserialize(byte[] data)
        {
            if (KindOf(data) != Kind.Turn) return null;
            try
            {
                int p = 4;
                var turn = ReadTurn(data, ref p);
                if (turn == null || p != data.Length) return null;   // trailing junk
                return turn;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        public static MatchSnapshot DeserializeSnapshot(byte[] data)
        {
            if (KindOf(data) != Kind.Snapshot) return null;
            try
            {
                int p = 4;
                var snap = new MatchSnapshot
                {
                    Tick = GetInt(data, ref p),
                    NextUnitId = GetInt(data, ref p),
                    NextNodeId = GetInt(data, ref p),
                    RngState = GetUInt(data, ref p),
                    Checksum = GetUInt(data, ref p),
                };

                int unitCount = GetInt(data, ref p);
                if (unitCount < 0 || unitCount > MaxUnits) return null;
                var units = new Unit[unitCount];
                for (int i = 0; i < unitCount; i++)
                {
                    var u = new Unit
                    {
                        Id = GetInt(data, ref p),
                        Owner = GetInt(data, ref p),
                        X = GetInt(data, ref p),
                        Y = GetInt(data, ref p),
                        Tx = GetInt(data, ref p),
                        Ty = GetInt(data, ref p),
                        Hp = GetInt(data, ref p),
                        MaxHp = GetInt(data, ref p),
                        TargetId = GetInt(data, ref p),
                        AttackTimer = GetInt(data, ref p),
                        Job = (Job)GetInt(data, ref p),
                        GatherNodeId = GetInt(data, ref p),
                        CarryType = (ResourceType)GetInt(data, ref p),
                        CarryAmount = GetInt(data, ref p),
                        GatherTimer = GetInt(data, ref p),
                    };

                    int remaining = GetInt(data, ref p);
                    if (remaining < 0 || remaining > MaxUnits) return null;
                    if (remaining > 0)
                    {
                        var path = new List<Tile>(remaining);
                        for (int j = 0; j < remaining; j++)
                            path.Add(new Tile(GetInt(data, ref p), GetInt(data, ref p)));
                        u.Path = path;
                        u.PathIndex = 0;
                    }
                    units[i] = u;
                }
                snap.Units = units;

                int nodeCount = GetInt(data, ref p);
                if (nodeCount < 0 || nodeCount > MaxUnits) return null;
                var nodes = new ResourceNode[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                {
                    nodes[i] = new ResourceNode
                    {
                        Id = GetInt(data, ref p),
                        Type = (ResourceType)GetInt(data, ref p),
                        X = GetInt(data, ref p),
                        Y = GetInt(data, ref p),
                        Amount = GetInt(data, ref p),
                    };
                }
                snap.Nodes = nodes;

                int stockCount = GetInt(data, ref p);
                if (stockCount < 0 || stockCount > MaxUnits) return null;
                var stock = new Dictionary<int, int[]>();
                for (int i = 0; i < stockCount; i++)
                {
                    int owner = GetInt(data, ref p);
                    int len = GetInt(data, ref p);
                    if (len < 0 || len > MaxUnitsPerCommand) return null;
                    var amts = new int[len];
                    for (int j = 0; j < len; j++) amts[j] = GetInt(data, ref p);
                    stock[owner] = amts;
                }
                snap.Stock = stock;

                int dropCount = GetInt(data, ref p);
                if (dropCount < 0 || dropCount > MaxUnits) return null;
                var drops = new Dictionary<int, Tile>();
                for (int i = 0; i < dropCount; i++)
                {
                    int owner = GetInt(data, ref p);
                    drops[owner] = new Tile(GetInt(data, ref p), GetInt(data, ref p));
                }
                snap.DropOffs = drops;

                int turnCount = GetInt(data, ref p);
                if (turnCount < 0 || turnCount > MaxPendingTurns) return null;
                var turns = new TurnInput[turnCount];
                for (int i = 0; i < turnCount; i++)
                {
                    turns[i] = ReadTurn(data, ref p);
                    if (turns[i] == null) return null;
                }
                snap.PendingTurns = turns;

                if (p != data.Length) return null;
                return snap;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        static TurnInput ReadTurn(byte[] data, ref int p)
        {
            var turn = new TurnInput
            {
                Owner = GetInt(data, ref p),
                Tick = GetInt(data, ref p),
                ChecksumTick = GetInt(data, ref p),
                Checksum = GetUInt(data, ref p),
            };

            int count = GetInt(data, ref p);
            if (count < 0 || count > MaxCommands) return null;

            var cmds = new Command[count];
            for (int i = 0; i < count; i++)
            {
                var c = new Command
                {
                    Owner = GetInt(data, ref p),
                    Type = (CommandType)GetInt(data, ref p),
                    X = GetInt(data, ref p),
                    Y = GetInt(data, ref p),
                    TargetId = GetInt(data, ref p),
                    ExecTick = GetInt(data, ref p),
                    Seq = GetInt(data, ref p),
                };
                int n = GetInt(data, ref p);
                if (n < 0 || n > MaxUnitsPerCommand) return null;
                var ids = new int[n];
                for (int j = 0; j < n; j++) ids[j] = GetInt(data, ref p);
                c.UnitIds = ids;
                cmds[i] = c;
            }

            turn.Commands = cmds;
            return turn;
        }

        static void PutInt(List<byte> b, int v) => PutUInt(b, unchecked((uint)v));

        static void PutUInt(List<byte> b, uint v)
        {
            b.Add((byte)(v & 0xff));
            b.Add((byte)((v >> 8) & 0xff));
            b.Add((byte)((v >> 16) & 0xff));
            b.Add((byte)((v >> 24) & 0xff));
        }

        static int GetInt(byte[] d, ref int p) => unchecked((int)GetUInt(d, ref p));

        static uint GetUInt(byte[] d, ref int p)
        {
            if (p + 4 > d.Length) throw new ArgumentOutOfRangeException(nameof(p));
            uint v = (uint)d[p] | ((uint)d[p + 1] << 8) |
                     ((uint)d[p + 2] << 16) | ((uint)d[p + 3] << 24);
            p += 4;
            return v;
        }
    }
}
