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
    public enum CommandType { Move = 0, Attack = 1, Gather = 2 }

    public enum ResourceType { Wood = 0, Stone = 1, Food = 2 }

    // What a unit is currently doing beyond just moving/fighting.
    public enum Job { None = 0, Gathering = 1 }

    // A harvestable deposit sitting on a tile. Depletes as it is gathered and is
    // removed when empty. Position is in whole tiles (a node occupies a cell),
    // never fixed-point — mixing the two is how something ends up a fraction of a
    // tile off from where the worker walks.
    public sealed class ResourceNode
    {
        public int Id;
        public ResourceType Type;
        public int X, Y;
        public int Amount;

        public ResourceNode Clone() => new ResourceNode
        {
            Id = Id, Type = Type, X = X, Y = Y, Amount = Amount,
        };
    }

    // Number of resource kinds, so a stockpile is a fixed-width int[].
    public static class Resources
    {
        public const int Count = 3;   // Wood, Stone, Food
    }

    public sealed class Command
    {
        public int Owner;
        public CommandType Type;
        public int[] UnitIds = Array.Empty<int>();
        public int X;         // Move: whole-number target (converted to fixed inside sim)
        public int Y;
        public int TargetId;  // Attack: the enemy unit to engage
        public int ExecTick;  // set by the lockstep layer
        public int Seq;       // set by the lockstep layer; unique per owner

        // Transports must put a command on the wire and rebuild it verbatim. Doing
        // that field-by-field at each call site is how a new field (Seq, say)
        // quietly goes missing on one path and desyncs only that peer — so the
        // copy lives here, next to the fields, and every transport uses it.
        public Command Clone() => new Command
        {
            Owner = Owner, Type = Type, UnitIds = UnitIds,
            X = X, Y = Y, TargetId = TargetId, ExecTick = ExecTick, Seq = Seq,
        };
    }

    public sealed class Unit
    {
        public int Id;
        public int Owner;
        public int X, Y;      // fixed-point position
        public int Tx, Ty;    // fixed-point position of the CURRENT waypoint
        public int Hp;
        public int MaxHp;

        // Combat. TargetId is the enemy this unit is engaging (0 = none); a unit
        // only fights once it has been given an Attack order, which is what keeps
        // Move-only scenarios (like the parity test) entirely out of combat.
        // AttackTimer counts ticks until the next blow may land.
        public int TargetId;
        public int AttackTimer;

        // Economy. A unit gathering carries up to a full load from a node back to
        // its owner's drop-off, then repeats. GatherNodeId is the assignment;
        // CarryType/CarryAmount is what it is hauling right now.
        public Job Job;
        public int GatherNodeId;
        public ResourceType CarryType;
        public int CarryAmount;
        public int GatherTimer;

        public bool Alive => Hp > 0;

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
                Id = Id, Owner = Owner, X = X, Y = Y, Tx = Tx, Ty = Ty,
                Hp = Hp, MaxHp = MaxHp, TargetId = TargetId, AttackTimer = AttackTimer,
                Job = Job, GatherNodeId = GatherNodeId, CarryType = CarryType,
                CarryAmount = CarryAmount, GatherTimer = GatherTimer,
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
        public readonly List<ResourceNode> Nodes = new(); // id order
        public readonly TileMap Map;
        int _nextId = 1;
        int _nextNodeId = 1;

        // Per-owner stockpiles and drop-off points. SortedDictionary so every
        // machine hashes owners in the same order — a plain Dictionary iterates in
        // insertion order, which two machines could reach differently.
        readonly SortedDictionary<int, int[]> _stock = new();
        readonly SortedDictionary<int, Tile> _dropOff = new();

        readonly int _speed = Fixed.One / 8;    // fixed-point units per tick
        readonly int _arriveEps = Fixed.One / 4;

        // --- Combat tuning (fixed-point where it measures distance) ------------
        const int SpawnHp = 100;
        static readonly int AttackRange = Fixed.One * 3 / 2;    // 1.5 tiles of reach
        static readonly int AggroRange = Fixed.FromInt(7);      // acquire the next foe within this
        const int AttackCooldown = 10;                          // ticks between blows (0.5s @ 20Hz)
        const int DamageMin = 8;
        const int DamageMax = 13;
        const int ChaseRepathEvery = 6;                         // ticks between chase re-paths

        // --- Economy tuning ---------------------------------------------------
        static readonly int GatherRange = Fixed.One * 3 / 2;    // reach to a node, 1.5 tiles
        const int CarryCapacity = 10;                           // load a worker hauls
        const int GatherInterval = 5;                           // ticks per 1 unit gathered

        // The default match seed. Both machines must seed identically, so this is
        // a fixed constant for now; a real lobby would agree one at match start
        // and pass it in. Only DAMAGE draws from the RNG, so a Move-only scenario
        // (the parity test) never touches it.
        public const uint DefaultSeed = 0xC0FFEE11u;
        readonly Rng _rng;

        // Scratch buffers for pathfinding, reused across calls. Working memory,
        // not game state: nothing here survives a call, so none of it is hashed.
        readonly PathFinder _pathFinder;
        readonly List<Tile> _rawPath = new();
        readonly List<Tile> _smoothPath = new();

        public Simulation() : this(TileMap.Open()) { }

        public Simulation(TileMap map, uint seed = DefaultSeed)
        {
            Map = map ?? TileMap.Open();
            _pathFinder = new PathFinder(Map);
            _rng = new Rng(seed);
        }

        // The RNG state is game state: two machines whose generators are one draw
        // apart agree until the next damage roll, then diverge. It is hashed into
        // StateChecksum and travels in a MatchSnapshot like everything else.
        public uint RngState => _rng.State;
        public void RestoreRng(uint state) => _rng.Restore(state);

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
        public void Restore(int tickNumber, int nextUnitId, uint rngState, IReadOnlyList<Unit> units,
                            int nextNodeId, IReadOnlyList<ResourceNode> nodes,
                            IReadOnlyDictionary<int, int[]> stock, IReadOnlyDictionary<int, Tile> dropOff)
        {
            TickNumber = tickNumber;
            _nextId = nextUnitId;
            _nextNodeId = nextNodeId;
            _rng.Restore(rngState);

            Units.Clear();
            foreach (var u in units) Units.Add(u.Clone());

            Nodes.Clear();
            foreach (var n in nodes) Nodes.Add(n.Clone());

            _stock.Clear();
            foreach (var kv in stock) _stock[kv.Key] = (int[])kv.Value.Clone();

            _dropOff.Clear();
            foreach (var kv in dropOff) _dropOff[kv.Key] = kv.Value;
        }

        // Read-only views for snapshotting. Sorted iteration is preserved, so a
        // snapshot serialises owners in a fixed order on every machine.
        public IReadOnlyList<ResourceNode> NodeList => Nodes;
        public IReadOnlyDictionary<int, int[]> Stockpiles => _stock;
        public IReadOnlyDictionary<int, Tile> DropOffs => _dropOff;

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
                Hp = SpawnHp,
                MaxHp = SpawnHp,
            };
            Units.Add(u);
            return u;
        }

        // ---- Economy setup & queries -----------------------------------------

        public ResourceNode SpawnNode(ResourceType type, int x, int y, int amount)
        {
            var n = new ResourceNode { Id = _nextNodeId++, Type = type, X = x, Y = y, Amount = amount };
            Nodes.Add(n);
            return n;
        }

        // Where an owner's gatherers deposit. Until there are buildings this
        // stands in for a keep/town-centre; set it identically on every machine.
        public void SetDropOff(int owner, int x, int y) => _dropOff[owner] = new Tile(x, y);

        public int Stockpile(int owner, ResourceType type) =>
            _stock.TryGetValue(owner, out var s) ? s[(int)type] : 0;

        public int NextNodeId => _nextNodeId;

        int[] StockOf(int owner)
        {
            if (!_stock.TryGetValue(owner, out var s)) { s = new int[Resources.Count]; _stock[owner] = s; }
            return s;
        }

        void Apply(Command cmd)
        {
            switch (cmd.Type)
            {
                case CommandType.Move:
                    foreach (var id in cmd.UnitIds)
                    {
                        var u = Units.Find(v => v.Id == id);
                        if (u != null && u.Owner == cmd.Owner)
                        {
                            StopWork(u);             // a plain move breaks off fighting AND gathering
                            Order(u, cmd.X, cmd.Y);
                        }
                    }
                    break;

                case CommandType.Attack:
                    var target = Units.Find(v => v.Id == cmd.TargetId);
                    // Only a living enemy is a valid target; a bad id is ignored
                    // rather than left to poison the combat phase.
                    if (target == null || !target.Alive || target.Owner == cmd.Owner) break;
                    foreach (var id in cmd.UnitIds)
                    {
                        var u = Units.Find(v => v.Id == id);
                        if (u != null && u.Owner == cmd.Owner && u.Id != target.Id)
                        {
                            u.Job = Job.None;         // stop gathering to go fight
                            u.TargetId = target.Id;   // the combat phase does the chasing/hitting
                        }
                    }
                    break;

                case CommandType.Gather:
                    // TargetId carries the node id for a Gather order. A worker can
                    // only be sent to a node the owner has a drop-off for, or the
                    // load it hauls would have nowhere to go.
                    var node = Nodes.Find(n => n.Id == cmd.TargetId);
                    if (node == null || !_dropOff.ContainsKey(cmd.Owner)) break;
                    foreach (var id in cmd.UnitIds)
                    {
                        var u = Units.Find(v => v.Id == id);
                        if (u != null && u.Owner == cmd.Owner)
                        {
                            u.TargetId = 0;           // stop fighting to go work
                            u.Job = Job.Gathering;
                            u.GatherNodeId = node.Id;
                            u.GatherTimer = 0;
                        }
                    }
                    break;
            }
        }

        // Cancel whatever task a unit was on. Called before a plain Move so an
        // order to reposition always wins over a standing job.
        static void StopWork(Unit u)
        {
            u.TargetId = 0;
            u.Job = Job.None;
            u.GatherNodeId = 0;
            u.GatherTimer = 0;
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

            ResolveCombat();
            RemoveDead();
            ResolveEconomy();
            TickNumber++;
        }

        // The gathering loop, iterated in id order (no RNG, pure integer state).
        // A worker cycles: walk to its node, gather to a full load, walk to its
        // owner's drop-off, deposit, repeat — until the node is empty. Only a unit
        // with Job.Gathering runs the body, so a match with no Gather orders (the
        // parity scenario) is untouched.
        void ResolveEconomy()
        {
            foreach (var u in Units)
            {
                if (u.Job != Job.Gathering) continue;

                var node = Nodes.Find(n => n.Id == u.GatherNodeId);
                bool nodeGone = node == null || node.Amount <= 0;
                bool full = u.CarryAmount >= CarryCapacity;

                if (full || (nodeGone && u.CarryAmount > 0))
                {
                    // Haul the load home.
                    var drop = _dropOff[u.Owner];
                    if (WithinRange(u, drop.X, drop.Y, GatherRange))
                    {
                        StockOf(u.Owner)[(int)u.CarryType] += u.CarryAmount;
                        u.CarryAmount = 0;
                        if (nodeGone) EndJob(u);
                        else Order(u, node.X, node.Y);      // back for another load
                    }
                    else ChaseTo(u, drop.X, drop.Y);
                }
                else if (!nodeGone)
                {
                    // Fill up at the node.
                    if (WithinRange(u, node.X, node.Y, GatherRange))
                    {
                        u.Path = null; u.PathIndex = 0; u.Tx = u.X; u.Ty = u.Y;   // stand and work
                        if (++u.GatherTimer >= GatherInterval)
                        {
                            u.GatherTimer = 0;
                            u.CarryType = node.Type;
                            u.CarryAmount++;
                            node.Amount--;
                        }
                    }
                    else ChaseTo(u, node.X, node.Y);
                }
                else
                {
                    EndJob(u);   // node gone and empty-handed
                }
            }

            Nodes.RemoveAll(n => n.Amount <= 0);
        }

        bool WithinRange(Unit u, int tileX, int tileY, int range) =>
            Fixed.VLen(Fixed.FromInt(tileX) - u.X, Fixed.FromInt(tileY) - u.Y) <= range;

        // Re-path toward a tile, but not every tick — same restraint as the combat
        // chase, so a dozen workers don't each run A* on every frame.
        void ChaseTo(Unit u, int tileX, int tileY)
        {
            if (!u.HasPath || TickNumber % ChaseRepathEvery == 0) Order(u, tileX, tileY);
        }

        static void EndJob(Unit u) { u.Job = Job.None; u.GatherNodeId = 0; u.GatherTimer = 0; }

        // The combat phase. Iterated in id order so RNG draws (damage rolls)
        // happen in a fixed sequence on every machine — the same discipline that
        // keeps command application deterministic keeps the dice deterministic.
        //
        // A unit only fights if it has a TargetId, which is only ever set by an
        // Attack command (or by acquiring the next foe after a kill). Move-only
        // units never enter this loop's body, so a Move-only match — the parity
        // scenario included — makes zero RNG draws and is completely unaffected.
        void ResolveCombat()
        {
            foreach (var u in Units)
            {
                if (u.AttackTimer > 0) u.AttackTimer--;
                if (u.TargetId == 0) continue;

                var target = Units.Find(v => v.Id == u.TargetId);
                if (target == null || !target.Alive)
                {
                    // Its foe is gone. Look for the next nearest one within aggro
                    // range; if there is none, stand down.
                    target = AcquireNearestEnemy(u);
                    u.TargetId = target?.Id ?? 0;
                    if (target == null) continue;
                }

                int dist = Fixed.VLen(target.X - u.X, target.Y - u.Y);

                if (dist <= AttackRange)
                {
                    // In reach: hold position and strike on cooldown.
                    u.Path = null;
                    u.PathIndex = 0;
                    u.Tx = u.X;
                    u.Ty = u.Y;

                    if (u.AttackTimer == 0)
                    {
                        target.Hp -= _rng.NextInt(DamageMin, DamageMax);
                        u.AttackTimer = AttackCooldown;
                    }
                }
                else
                {
                    // Out of reach: close the distance. Re-path periodically so a
                    // moving target is still chased, but not every tick — that
                    // would run A* for every fighting unit on every frame.
                    bool needsPath = !u.HasPath || TickNumber % ChaseRepathEvery == 0;
                    if (needsPath)
                        Order(u, Fixed.ToInt(target.X), Fixed.ToInt(target.Y));
                }
            }
        }

        // Nearest living enemy within aggro range, ties broken by id so every
        // machine acquires the same one. No RNG here — acquisition is pure
        // geometry, only the damage roll is random.
        Unit AcquireNearestEnemy(Unit u)
        {
            Unit best = null;
            int bestDist = int.MaxValue;
            foreach (var v in Units)
            {
                if (v.Owner == u.Owner || !v.Alive) continue;
                int dist = Fixed.VLen(v.X - u.X, v.Y - u.Y);
                if (dist > AggroRange) continue;
                if (dist < bestDist) { bestDist = dist; best = v; }
                // ties: keep the lower id, which is whichever we already have,
                // since Units is walked in id order.
            }
            return best;
        }

        // Clear the dead, in id order so the surviving list stays id-ordered.
        // Done as one pass after all attacks resolve, so within a tick a unit
        // that drops to 0 still counts as present for everyone else's targeting
        // that same tick — order of resolution can't change who dies.
        void RemoveDead() => Units.RemoveAll(u => !u.Alive);

        // -1 while both sides still have units, 0 if everyone is dead (a mutual
        // wipe), otherwise the owner id of the last side standing. The engine
        // decides what to DO with this; the sim only reports it.
        public int MatchWinner()
        {
            int owner = -1;
            foreach (var u in Units)
            {
                if (!u.Alive) continue;
                if (owner == -1) owner = u.Owner;
                else if (owner != u.Owner) return -1;   // two sides alive: ongoing
            }
            return owner == -1 ? 0 : owner;             // -1 became "nobody alive" -> draw
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
            Mix(_nextNodeId);
            Mix(unchecked((int)_rng.State));   // the dice must be in the same place

            foreach (var u in Units)
            {
                Mix(u.Id); Mix(u.Owner); Mix(u.X); Mix(u.Y);
                Mix(u.Hp); Mix(u.MaxHp);
                Mix(u.Tx); Mix(u.Ty);
                Mix(u.TargetId); Mix(u.AttackTimer);
                Mix((int)u.Job); Mix(u.GatherNodeId);
                Mix((int)u.CarryType); Mix(u.CarryAmount); Mix(u.GatherTimer);

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

            foreach (var n in Nodes)                 // id order
            {
                Mix(n.Id); Mix((int)n.Type); Mix(n.X); Mix(n.Y); Mix(n.Amount);
            }

            foreach (var kv in _stock)               // SortedDictionary -> owner order
            {
                Mix(kv.Key);
                foreach (int amt in kv.Value) Mix(amt);
            }

            foreach (var kv in _dropOff)             // owner order
            {
                Mix(kv.Key); Mix(kv.Value.X); Mix(kv.Value.Y);
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
