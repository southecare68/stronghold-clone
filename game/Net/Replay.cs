// Replay.cs — Record a match, play it back bit-for-bit.
//
// Lockstep hands this to us almost for free. A whole match is determined by three
// things: the terrain, the starting state, and the ordered stream of commands.
// Record those and the deterministic simulation reproduces the match EXACTLY —
// same positions, same dice, same winner. No unit state is stored per tick, only
// the commands that drove it, so a long game is a small file.
//
// Two payoffs beyond "watch your game back":
//   * It is a determinism check. A replay whose final checksum differs from the
//     live run is a desync the recorder caught — the same guarantee SimParity and
//     the cross-architecture run rest on, now available for any real match.
//   * It is a debugging superpower. A desync seen between two machines can be
//     reproduced on ONE machine from the recorded commands, then bisected.
//
// Engine-agnostic on purpose (no Godot): the same recording plays back headlessly
// in a test or on screen in the game.

using System;
using System.Collections.Generic;
using Sim;

namespace Netcode
{
    public sealed class Replay
    {
        const byte Magic0 = (byte)'S';
        const byte Magic1 = (byte)'R';   // "Stronghold Replay"
        const byte Version = 1;

        // The battlefield, so a replay can rebuild the exact map (a replay on a
        // different map would diverge at tick 0).
        public int Width;
        public int Height;
        public Terrain[] Terrain = Array.Empty<Terrain>();

        // The world before the first recorded tick, and the commands executed on
        // each tick from there on. Commands[i] drove tick (StartTick + i).
        public MatchSnapshot Initial;
        public int StartTick;
        public readonly List<Command[]> Commands = new();

        // What the live run ended on — the yardstick a playback is checked against.
        public int FinalTick;
        public uint FinalChecksum;

        // Rebuild the starting simulation: same terrain, same initial state.
        public Simulation Reconstruct()
        {
            var map = TileMap.FromTiles(Width, Height, Terrain);
            var sim = new Simulation(map);
            if (Initial != null) sim.Restore(Initial);
            return sim;
        }

        // Play the whole match through and return the final state checksum. Equal
        // to FinalChecksum means the replay reproduced the run exactly.
        public uint Play()
        {
            var sim = Reconstruct();
            foreach (var cmds in Commands) sim.Tick(cmds);
            return sim.StateChecksum();
        }

        // Did this replay reproduce the recorded run bit-for-bit?
        public bool Verify() => Play() == FinalChecksum;

        // ---- Persistence -----------------------------------------------------

        public byte[] Serialize()
        {
            var buf = new List<byte>(1024);
            buf.Add(Magic0); buf.Add(Magic1); buf.Add(Version); buf.Add(0);

            Wire.PutInt(buf, Width);
            Wire.PutInt(buf, Height);
            foreach (var t in Terrain) buf.Add((byte)t);

            Wire.PutInt(buf, StartTick);
            Wire.PutInt(buf, FinalTick);
            Wire.PutUInt(buf, FinalChecksum);

            // The initial snapshot, embedded as its own length-prefixed block so
            // the snapshot format can evolve without disturbing the replay format.
            byte[] snap = Wire.Serialize(Initial);
            Wire.PutInt(buf, snap.Length);
            buf.AddRange(snap);

            Wire.PutInt(buf, Commands.Count);
            foreach (var cmds in Commands)
            {
                Wire.PutInt(buf, cmds.Length);
                foreach (var c in cmds) Wire.WriteCommand(buf, c);
            }
            return buf.ToArray();
        }

        // Returns null for anything malformed — a replay half-read is worse than
        // one refused.
        public static Replay Deserialize(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 4) return null;
                if (data[0] != Magic0 || data[1] != Magic1 || data[2] != Version) return null;
                int p = 4;

                var r = new Replay
                {
                    Width = Wire.GetInt(data, ref p),
                    Height = Wire.GetInt(data, ref p),
                };
                int tileCount = r.Width * r.Height;
                if (tileCount < 0 || tileCount > 1 << 24 || p + tileCount > data.Length) return null;
                r.Terrain = new Terrain[tileCount];
                for (int i = 0; i < tileCount; i++) r.Terrain[i] = (Terrain)data[p++];

                r.StartTick = Wire.GetInt(data, ref p);
                r.FinalTick = Wire.GetInt(data, ref p);
                r.FinalChecksum = Wire.GetUInt(data, ref p);

                int snapLen = Wire.GetInt(data, ref p);
                if (snapLen < 0 || p + snapLen > data.Length) return null;
                var snapBytes = new byte[snapLen];
                Array.Copy(data, p, snapBytes, 0, snapLen);
                p += snapLen;
                r.Initial = Wire.DeserializeSnapshot(snapBytes);
                if (r.Initial == null) return null;

                int tickCount = Wire.GetInt(data, ref p);
                if (tickCount < 0 || tickCount > 1 << 24) return null;
                for (int i = 0; i < tickCount; i++)
                {
                    int n = Wire.GetInt(data, ref p);
                    if (n < 0 || n > 4096) return null;
                    var cmds = new Command[n];
                    for (int j = 0; j < n; j++)
                    {
                        cmds[j] = Wire.ReadCommand(data, ref p);
                        if (cmds[j] == null) return null;
                    }
                    r.Commands.Add(cmds);
                }

                if (p != data.Length) return null;   // trailing junk
                return r;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    // Captures a match as it runs. Snapshots the world at construction (the
    // starting point), then records the commands handed to each tick.
    //
    //     var rec = new ReplayRecorder(sim);
    //     while (...) { rec.Record(commands); sim.Tick(commands); }
    //     Replay replay = rec.Finish(sim);
    public sealed class ReplayRecorder : ITickRecorder
    {
        readonly Replay _replay;

        public ReplayRecorder(Simulation sim)
        {
            _replay = new Replay
            {
                Width = sim.Map.Width,
                Height = sim.Map.Height,
                Terrain = sim.Map.CopyTiles(),
                Initial = sim.Snapshot(),
                StartTick = sim.TickNumber,
            };
        }

        // Record the exact command set about to drive one tick. Cloned, so later
        // mutation of the caller's list can't rewrite history.
        public void Record(IReadOnlyList<Command> commands)
        {
            var arr = new Command[commands.Count];
            for (int i = 0; i < commands.Count; i++) arr[i] = commands[i].Clone();
            _replay.Commands.Add(arr);
        }

        public Replay Finish(Simulation sim)
        {
            _replay.FinalTick = sim.TickNumber;
            _replay.FinalChecksum = sim.StateChecksum();
            return _replay;
        }
    }
}
