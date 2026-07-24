// Lockstep.cs — Deterministic lockstep with input delay.
// Port of prototype-node/src/lockstep.js, extended for real networking.
//
// Each player runs a full Client (its own Simulation). A command issued on tick
// N executes on tick N + InputDelay, giving it time to reach everyone. Only
// commands cross the network — never unit state.
//
// THE UNIT OF THE PROTOCOL IS A TURN, NOT A COMMAND.
// Every player sends exactly one TurnInput for every tick, even when they did
// nothing — "no commands this tick" is an explicit message. That is not
// redundancy: silence is indistinguishable from a packet that hasn't arrived
// yet, so a client that guessed would run a tick its peer didn't and desync.
// With explicit turns, a client simply refuses to advance until it holds every
// player's input for the tick it is about to run. That refusal IS the stall.
//
// The ITransport seam is the ONE place real networking plugs in.
// LoopbackTransport (below) delivers turns in-process for the single-window
// demo; EnetTransport (game/Scripts/, Godot layer) is the same interface over a
// real socket. The Client cannot tell them apart.

using System;
using System.Collections.Generic;

namespace Sim
{
    // One player's complete input for one tick.
    public sealed class TurnInput
    {
        public int Owner;
        public int Tick;                                  // tick these commands execute on
        public Command[] Commands = Array.Empty<Command>();

        // The sender's state hash for a tick it has ALREADY completed, piggybacked
        // on every turn. Costs 8 bytes and turns "the game slowly goes wrong" into
        // "DESYNC at tick 412", which is the difference between a debuggable bug
        // and a haunted one. -1 means "I haven't completed a tick yet".
        public int ChecksumTick = -1;
        public uint Checksum;

        public TurnInput Clone()
        {
            var copy = new Command[Commands.Length];
            for (int i = 0; i < Commands.Length; i++) copy[i] = Commands[i].Clone();
            return new TurnInput
            {
                Owner = Owner, Tick = Tick, Commands = copy,
                ChecksumTick = ChecksumTick, Checksum = Checksum,
            };
        }
    }

    // Everything a returning player needs to join a match already in progress.
    //
    // A client that has been disconnected cannot replay the ticks it missed —
    // it never received the commands — so it is handed the result instead. The
    // snapshot is pure integer state, exactly like the simulation it came from,
    // so adopting it lands the rejoiner on a bit-identical world rather than an
    // approximately similar one.
    public sealed class MatchSnapshot
    {
        public int Tick;
        public int NextUnitId;
        public int NextNodeId;
        public uint RngState;      // the sim's random generator, mid-sequence
        public Unit[] Units = Array.Empty<Unit>();
        public ResourceNode[] Nodes = Array.Empty<ResourceNode>();
        public Dictionary<int, int[]> Stock = new();       // owner -> per-resource amounts
        public Dictionary<int, Tile> DropOffs = new();     // owner -> drop-off tile

        // Turns the sender has ALREADY published for ticks at or after Tick.
        // Input delay means a client commits to turns several ticks ahead, and it
        // will not send them again — without these the rejoiner would wait
        // forever for input that was already spoken for.
        public TurnInput[] PendingTurns = Array.Empty<TurnInput>();

        // The sender's checksum at Tick. The rejoiner recomputes it after
        // adopting, so a snapshot that arrives wrong is caught at the moment of
        // the join rather than becoming a desync a hundred ticks later.
        public uint Checksum;
    }

    // What a peer disagreed about, captured the moment it happened.
    public sealed class DesyncReport
    {
        public int Tick;
        public int RemotePlayer;
        public uint LocalChecksum;
        public uint RemoteChecksum;

        public override string ToString() =>
            $"DESYNC at tick {Tick}: local 0x{LocalChecksum:X8} != player " +
            $"{RemotePlayer} 0x{RemoteChecksum:X8}";
    }

    public interface ITransport
    {
        // Deliver one player's turn to every participant, the sender included.
        void Send(TurnInput turn);

        // Pump the underlying socket. No-op in-process.
        void Poll();

        // How many players must be heard from before a tick may run. A client
        // that gets this wrong either stalls forever or runs ahead of a peer, so
        // a networked transport must not report the full count until everyone
        // has actually connected.
        int PlayerCount { get; }
    }

    public sealed class Client
    {
        public const int InputDelay = 3;

        // How much tick history to keep. Enough to answer a peer whose checksum
        // report is a few ticks behind, small enough that a long match doesn't
        // grow without bound.
        const int HistoryTicks = 256;

        public readonly int PlayerId;
        public readonly Simulation Sim;

        readonly ITransport _net;
        readonly List<Command> _pending = new();                     // issued, not yet sent
        readonly Dictionary<int, List<TurnInput>> _turns = new();    // tick -> turns, kept sorted by Owner
        readonly Dictionary<int, uint> _checksums = new();           // our own completed-tick history

        int _nextSeq = 1;
        int _sentThrough = -1;   // highest tick we have published a turn for

        // True when the last TryStep could not advance because a peer's turn for
        // the current tick had not arrived. This is normal on a lossy link; it is
        // only a problem if it persists.
        public bool Stalled { get; private set; }

        // Set once, the first time a peer reports a checksum that disagrees with
        // ours. Never cleared — after a desync the two worlds have already parted
        // and everything downstream is noise.
        public DesyncReport Desync { get; private set; }

        public Client(int playerId, ITransport net) : this(playerId, net, null) { }

        // Start the client's simulation on a specific map. Every client in a
        // match must be given the identical map — StateChecksum mixes in the
        // map's fingerprint, so two clients handed different terrain are caught
        // on the first comparison rather than when their units first diverge.
        public Client(int playerId, ITransport net, TileMap map)
        {
            PlayerId = playerId;
            _net = net;
            Sim = map != null ? new Simulation(map) : new Simulation();
        }

        // Player intent now; runs InputDelay ticks in the future on all machines.
        // The command is not published until the next SendInput, which is what
        // lets a whole frame's worth of clicks ride in one turn.
        public void Issue(Command cmd)
        {
            cmd.Owner = PlayerId;
            cmd.Seq = _nextSeq++;
            _pending.Add(cmd);
        }

        // Publish our input for the tick InputDelay ahead. Call once per tick, on
        // every client, BEFORE anyone calls TryStep — a client cannot advance
        // until its peers have spoken, so in a single process that drives several
        // clients, all of them must speak first.
        public void SendInput()
        {
            int target = Sim.TickNumber + InputDelay;

            // On the first call this also covers ticks 0..InputDelay-1, which no
            // one could have sent input for: the match starts with everyone
            // having already agreed to do nothing for those ticks.
            for (int t = _sentThrough + 1; t <= target; t++)
            {
                var turn = new TurnInput
                {
                    Owner = PlayerId,
                    Tick = t,
                    Commands = t == target ? TakePending() : Array.Empty<Command>(),
                    ChecksumTick = Sim.TickNumber - 1,
                };
                if (turn.ChecksumTick >= 0 &&
                    _checksums.TryGetValue(turn.ChecksumTick, out var mine))
                    turn.Checksum = mine;
                else
                    turn.ChecksumTick = -1;

                foreach (var c in turn.Commands) c.ExecTick = t;
                _net.Send(turn);
            }
            _sentThrough = target;
        }

        // Advance exactly one tick, but ONLY if every player's input for it is in
        // hand. Returns false without touching the simulation otherwise.
        public bool TryStep()
        {
            _net.Poll();

            if (!HasAllInput(Sim.TickNumber))
            {
                Stalled = true;
                return false;
            }
            Stalled = false;

            int tick = Sim.TickNumber;
            var cmds = new List<Command>();
            // Turns are held sorted by owner, so this gathering order is the same
            // on every machine — no hash-order iteration anywhere near the sim.
            // (Simulation.Tick re-sorts into canonical order regardless.)
            foreach (var turn in _turns[tick])
                cmds.AddRange(turn.Commands);

            Sim.Tick(cmds);

            // StateChecksum, not Checksum: the network must compare EVERYTHING
            // that can diverge (orders, paths, RNG, combat), not just the frozen
            // units-only hash the parity test guards. Checksum() would miss a
            // desync in any of the newer state.
            _checksums[tick] = Sim.StateChecksum();
            Forget(tick - HistoryTicks);
            return true;
        }

        // Do we hold every player's input for this tick?
        public bool HasAllInput(int tick) =>
            _turns.TryGetValue(tick, out var list) && list.Count >= _net.PlayerCount;

        // ---- Rejoining a match in progress ----------------------------------

        // Package up everything a returning player needs. Taken by whoever is
        // already in the match (the host) at the moment someone connects.
        public MatchSnapshot CaptureSnapshot()
        {
            var units = new Unit[Sim.Units.Count];
            for (int i = 0; i < units.Length; i++) units[i] = Sim.Units[i].Clone();

            var nodes = new ResourceNode[Sim.NodeList.Count];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = Sim.NodeList[i].Clone();

            var stock = new Dictionary<int, int[]>();
            foreach (var kv in Sim.Stockpiles) stock[kv.Key] = (int[])kv.Value.Clone();

            var drops = new Dictionary<int, Tile>();
            foreach (var kv in Sim.DropOffs) drops[kv.Key] = kv.Value;

            // Our own turns from the current tick onward. We published these
            // already and will never publish them again — _sentThrough has moved
            // past them — so if we don't hand them over now, nobody ever will.
            var pending = new List<TurnInput>();
            for (int t = Sim.TickNumber; t <= _sentThrough; t++)
            {
                if (!_turns.TryGetValue(t, out var list)) continue;
                foreach (var turn in list)
                    if (turn.Owner == PlayerId) pending.Add(turn.Clone());
            }

            return new MatchSnapshot
            {
                Tick = Sim.TickNumber,
                NextUnitId = Sim.NextUnitId,
                NextNodeId = Sim.NextNodeId,
                RngState = Sim.RngState,
                Units = units,
                Nodes = nodes,
                Stock = stock,
                DropOffs = drops,
                PendingTurns = pending.ToArray(),
                Checksum = Sim.StateChecksum(),
            };
        }

        // Adopt a match already in progress. Returns false — and reports a
        // desync — if the state we rebuilt doesn't hash to what the sender said
        // it should, because a rejoiner that silently starts from a subtly wrong
        // world is the worst outcome available.
        public bool AdoptSnapshot(MatchSnapshot snap)
        {
            Sim.Restore(snap.Tick, snap.NextUnitId, snap.RngState, snap.Units,
                        snap.NextNodeId, snap.Nodes, snap.Stock, snap.DropOffs);

            // Everything from before the join is meaningless now: turns for ticks
            // we will never run, checksums for a world we were not in, and any
            // commands we queued while disconnected.
            _turns.Clear();
            _checksums.Clear();
            _pending.Clear();

            // Publish from the snapshot's tick onward. SendInput fills the gap
            // between here and the input-delay horizon with empty turns, which is
            // exactly right: we were not here, so we did nothing.
            _sentThrough = snap.Tick - 1;

            foreach (var turn in snap.PendingTurns) Receive(turn);

            uint mine = Sim.StateChecksum();
            if (mine == snap.Checksum) return true;

            Desync = new DesyncReport
            {
                Tick = snap.Tick,
                RemotePlayer = 0,               // 0 = "whoever sent the snapshot"
                LocalChecksum = mine,
                RemoteChecksum = snap.Checksum,
            };
            return false;
        }

        // Called by the transport when ANY player's turn arrives, ours included.
        // Idempotent: a turn redelivered (a relaying host, a duplicated datagram)
        // replaces the identical one already stored rather than double-applying.
        public void Receive(TurnInput turn)
        {
            if (!_turns.TryGetValue(turn.Tick, out var list))
            {
                list = new List<TurnInput>();
                _turns[turn.Tick] = list;
            }

            int i = 0;
            while (i < list.Count && list[i].Owner < turn.Owner) i++;
            if (i < list.Count && list[i].Owner == turn.Owner) list[i] = turn;
            else list.Insert(i, turn);

            CheckAgainst(turn);
        }

        // Compare a peer's reported checksum with what we computed for that tick.
        void CheckAgainst(TurnInput turn)
        {
            if (Desync != null || turn.Owner == PlayerId || turn.ChecksumTick < 0) return;
            if (!_checksums.TryGetValue(turn.ChecksumTick, out var mine)) return;
            if (mine == turn.Checksum) return;

            Desync = new DesyncReport
            {
                Tick = turn.ChecksumTick,
                RemotePlayer = turn.Owner,
                LocalChecksum = mine,
                RemoteChecksum = turn.Checksum,
            };
        }

        Command[] TakePending()
        {
            if (_pending.Count == 0) return Array.Empty<Command>();
            var arr = _pending.ToArray();
            _pending.Clear();
            return arr;
        }

        void Forget(int tick)
        {
            if (tick < 0) return;
            _turns.Remove(tick);
            _checksums.Remove(tick);
        }
    }

    // In-process, loss-free transport so the game runs in one window today.
    public sealed class LoopbackTransport : ITransport
    {
        readonly List<Client> _clients = new();
        public void Connect(Client c) => _clients.Add(c);

        public int PlayerCount => _clients.Count;
        public void Poll() { }

        public void Send(TurnInput turn)
        {
            foreach (var c in _clients) c.Receive(turn.Clone());
        }
    }
}
