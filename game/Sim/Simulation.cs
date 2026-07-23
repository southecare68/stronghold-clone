// Simulation.cs — The engine-agnostic deterministic game state.
// 1:1 port of prototype-node/src/simulation.js.
//
// Knows nothing about Godot, rendering, or the network. Pure state machine:
//     sim.Tick(commandsForThisTick)  ->  new state
// Same start + same commands => same result on every machine, every run.
//
// PORT VERIFICATION: because this mirrors the verified JS byte-for-byte, running
// the same scenario should produce the SAME checksum stream as the Node
// prototype (final checksum 0xB1A7A676 for that scenario). That equality is a
// ready-made unit test — see CONTEXT_HANDOFF.md.

using System;
using System.Collections.Generic;

namespace Sim
{
    public enum CommandType { Move = 0 }

    public sealed class Command
    {
        public int Owner;
        public CommandType Type;
        public int[] UnitIds = Array.Empty<int>();
        public int X;         // whole-number target (converted to fixed inside sim)
        public int Y;
        public int ExecTick;  // set by the lockstep layer
        public int Seq;       // set by the lockstep layer; unique per owner

        // Transports must put a command on the wire and rebuild it verbatim. Doing
        // that field-by-field at each call site is how a new field (Seq, say)
        // quietly goes missing on one path and desyncs only that peer — so the
        // copy lives here, next to the fields, and every transport uses it.
        public Command Clone() => new Command
        {
            Owner = Owner, Type = Type, UnitIds = UnitIds,
            X = X, Y = Y, ExecTick = ExecTick, Seq = Seq,
        };
    }

    public sealed class Unit
    {
        public int Id;
        public int Owner;
        public int X, Y;      // fixed-point position
        public int Tx, Ty;    // fixed-point position of the CURRENT waypoint
        public int Hp;

        // The route still to walk, and how far along it we are. Tx/Ty always
        // mirror Path[PathIndex], so the movement integrator below never needs to
        // know a path exists — it just walks toward a point, as it always did.
        public List<Tile> Path;
        public int PathIndex;

        public bool HasPath => Path != null && PathIndex < Path.Count;

        public Unit Clone()
        {
            var copy = new Unit
            {
                Id = Id, Owner = Owner, X = X, Y = Y, Tx = Tx, Ty = Ty, Hp = Hp,
                PathIndex = PathIndex,
            };
            if (Path != null) copy.Path = new List<Tile>(Path);
            return copy;
        }
    }

    public sealed class Simulation
    {
        public int TickNumber;
        public readonly List<Unit> Units = new(); // always iterated in id order
        public readonly TileMap Map;
        int _nextId = 1;

        readonly int _speed = Fixed.One / 8;    // fixed-point units per tick
        readonly int _arriveEps = Fixed.One / 4;

        // Scratch buffers for pathfinding, reused across calls. Working memory,
        // not game state: nothing here survives a call, so none of it is hashed.
        readonly PathFinder _pathFinder;
        readonly List<Tile> _rawPath = new();
        readonly List<Tile> _smoothPath = new();

        public Simulation() : this(TileMap.Open()) { }

        public Simulation(TileMap map)
        {
            Map = map ?? TileMap.Open();
            _pathFinder = new PathFinder(Map);
        }

        // Part of the state, not an implementation detail: two machines that
        // disagree about the next id would name the next spawned unit
        // differently and diverge forever. It travels in a snapshot with
        // everything else.
        public int NextUnitId => _nextId;

        // Replace this simulation's entire state with another's. Used only when a
        // reconnecting player adopts the ongoing match — a client that has been
        // away cannot replay the ticks it missed, so it is handed the result.
        //
        // The unit ORDER is part of the state, not a detail: Tick and Checksum
        // both walk the list in place, so a restored list in a different order is
        // a different world with the same contents.
        public void Restore(int tickNumber, int nextUnitId, IReadOnlyList<Unit> units)
        {
            TickNumber = tickNumber;
            _nextId = nextUnitId;
            Units.Clear();
            foreach (var u in units) Units.Add(u.Clone());
        }

        public Unit SpawnUnit(int owner, int xInt, int yInt)
        {
            var u = new Unit
            {
                Id = _nextId++,
                Owner = owner,
                X = Fixed.FromInt(xInt),
                Y = Fixed.FromInt(yInt),
                Tx = Fixed.FromInt(xInt),
                Ty = Fixed.FromInt(yInt),
                Hp = 100,
            };
            Units.Add(u);
            return u;
        }

        void Apply(Command cmd)
        {
            if (cmd.Type == CommandType.Move)
            {
                foreach (var id in cmd.UnitIds)
                {
                    var u = Units.Find(v => v.Id == id);
                    if (u != null && u.Owner == cmd.Owner) Order(u, cmd.X, cmd.Y);
                }
            }
        }

        // Turn "go there" into a route. A click outside the world is clamped to
        // the edge rather than refused — players drag-select and fling orders at
        // the screen edge constantly, and a silently ignored order feels broken.
        // A click on rock or water IS refused, and the unit keeps its previous
        // orders; walking to the nearest reachable tile instead would be kinder
        // and is worth doing once there is a UI to explain it.
        void Order(Unit u, int goalX, int goalY)
        {
            int gx = Clamp(goalX, 0, Map.Width - 1);
            int gy = Clamp(goalY, 0, Map.Height - 1);
            if (!Map.Passable(gx, gy)) return;

            int sx = Fixed.ToInt(u.X);
            int sy = Fixed.ToInt(u.Y);

            _rawPath.Clear();
            if (!_pathFinder.TryFindPath(sx, sy, gx, gy, _rawPath)) return;

            Smooth(sx, sy, _rawPath, _smoothPath);

            // Standing on the goal tile already: the route is empty, but the unit
            // may still need to cross the tile to the exact spot asked for.
            if (_smoothPath.Count == 0) _smoothPath.Add(new Tile(gx, gy));

            u.Path = new List<Tile>(_smoothPath);
            u.PathIndex = 0;
            AimAtWaypoint(u);
        }

        // String-pulling. A* returns a route tile by tile, which makes units
        // zigzag across open ground following corners no one asked for. Walk
        // forward to the FARTHEST tile still reachable by a clear run — straight,
        // unobstructed, and over nothing costlier than ground — keep that, and
        // discard everything in between. The "over ground only" part is what
        // stops smoothing from straightening a marsh detour back through the
        // marsh; see TileMap.HasClearRun.
        //
        // This is also what protects the parity constant: on open ground the very
        // first check sees the destination directly, so the whole route collapses
        // to one waypoint and the movement maths is bit-identical to what the
        // simulation did before it could path at all.
        void Smooth(int fromX, int fromY, List<Tile> raw, List<Tile> smoothed)
        {
            smoothed.Clear();
            int cx = fromX, cy = fromY;
            int i = 0;

            while (i < raw.Count)
            {
                int j = raw.Count - 1;
                while (j > i && !Map.HasClearRun(cx, cy, raw[j].X, raw[j].Y)) j--;

                smoothed.Add(raw[j]);
                cx = raw[j].X;
                cy = raw[j].Y;
                i = j + 1;
            }
        }

        void AimAtWaypoint(Unit u)
        {
            if (!u.HasPath) return;
            var w = u.Path[u.PathIndex];
            u.Tx = Fixed.FromInt(w.X);
            u.Ty = Fixed.FromInt(w.Y);
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Advance exactly one tick using the full agreed command set for it.
        public void Tick(IReadOnlyList<Command> commands)
        {
            var ordered = new List<Command>(commands);
            ordered.Sort(CanonicalOrder); // same order on every machine
            foreach (var c in ordered) Apply(c);

            foreach (var u in Units)
            {
                int dx = u.Tx - u.X;
                int dy = u.Ty - u.Y;
                int dist = Fixed.VLen(dx, dy);
                if (dist > _arriveEps)
                {
                    int step = dist < _speed ? dist : _speed;
                    u.X += Fixed.Div(Fixed.Mul(dx, step), dist);
                    u.Y += Fixed.Div(Fixed.Mul(dy, step), dist);
                }
                else
                {
                    u.X = u.Tx;
                    u.Y = u.Ty;

                    // Arrived at this waypoint — aim at the next leg, or drop the
                    // route if that was the last one. A unit with a single
                    // waypoint (anything on open ground) falls straight through
                    // to "route finished", which is what it did before paths
                    // existed.
                    if (u.HasPath && u.PathIndex + 1 < u.Path.Count)
                    {
                        u.PathIndex++;
                        AimAtWaypoint(u);
                    }
                    else
                    {
                        u.Path = null;
                        u.PathIndex = 0;
                    }
                }
            }
            TickNumber++;
        }

        // 32-bit FNV-1a over tick number and unit position/health, and NOTHING
        // ELSE — this hash is FROZEN.
        //
        // It is the number the Node prototype produces (0xB1A7A676 for the
        // reference scenario), and tests/SimParity compares against it to prove
        // the movement core still behaves exactly as the verified original. Add a
        // field here and that proof is gone, permanently, because there is no way
        // back to a constant once it has drifted.
        //
        // New game state goes in StateChecksum() below. That is the one the
        // network actually compares; this one is a regression guard on the oldest
        // and most-verified part of the simulation.
        public uint Checksum()
        {
            uint h = 0x811c9dc5;
            void Mix(int n)
            {
                for (int i = 0; i < 4; i++)
                {
                    h ^= (uint)((n >> (i * 8)) & 0xff);
                    h *= 0x01000193;
                }
            }
            Mix(TickNumber);
            foreach (var u in Units)
            {
                Mix(u.Id); Mix(u.Owner); Mix(u.X); Mix(u.Y); Mix(u.Hp);
            }
            return h;
        }

        // Everything that can diverge. THIS is what turns piggyback and what
        // desync detection compares, so anything added to the simulation from
        // here on gets mixed in here — orders, stockpiles, buildings, RNG state.
        // A field that is game state but is missing from this hash is a desync
        // that goes unreported until it changes something visible, which may be
        // minutes later and nowhere near the cause.
        public uint StateChecksum()
        {
            uint h = 0x811c9dc5;
            void Mix(int n)
            {
                for (int i = 0; i < 4; i++)
                {
                    h ^= (uint)((n >> (i * 8)) & 0xff);
                    h *= 0x01000193;
                }
            }

            // Terrain is not hashed per tick — it never changes — but the two
            // machines had better be on the same map. One number covers it.
            Mix(unchecked((int)Map.Fingerprint));
            Mix(TickNumber);
            Mix(_nextId);

            foreach (var u in Units)
            {
                Mix(u.Id); Mix(u.Owner); Mix(u.X); Mix(u.Y); Mix(u.Hp);
                Mix(u.Tx); Mix(u.Ty);

                // The route still to walk. Two units in identical positions with
                // different plans are not in the same world.
                int remaining = u.HasPath ? u.Path.Count - u.PathIndex : 0;
                Mix(remaining);
                for (int i = u.PathIndex; i < remaining + u.PathIndex; i++)
                {
                    Mix(u.Path[i].X);
                    Mix(u.Path[i].Y);
                }
            }
            return h;
        }

        // A TOTAL order — no two distinct commands may ever compare equal.
        //
        // (Owner, Seq) is unique: Seq is handed out by the issuing client, and a
        // player's commands are only ever issued by that player's client. Ties are
        // the whole danger here. List<T>.Sort leaves tied elements in the order
        // they arrived, and arrival order is exactly what differs between machines
        // once a real network replaces LoopbackTransport — two peers would apply
        // the same tick's commands in different sequences and silently drift apart.
        // tests/CommandOrder holds that case down.
        //
        // Type is deliberately NOT a sort key: a player's own commands must apply
        // in the order that player issued them, never regrouped by type.
        static int CanonicalOrder(Command a, Command b)
        {
            if (a.Owner != b.Owner) return a.Owner - b.Owner;
            return a.Seq.CompareTo(b.Seq);
        }
    }
}
