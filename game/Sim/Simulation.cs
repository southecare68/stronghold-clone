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
    public enum CommandType
    {
        Move = 0, Attack = 1, Gather = 2, Build = 3, Train = 4, ToggleGate = 5, AttackBuilding = 6,
    }

    public enum BuildingType { Keep = 0, Barracks = 1, Wall = 2, Gatehouse = 3, WoodcutterHut = 4, Storehouse = 5, Quarry = 6, Farm = 7, Mill = 8, Bakery = 9 }

    // Wood and Stone are gathered from the map; Food is the goal resource that
    // feeds an army. Grain and Flour are the food chain's intermediates — a farm
    // grows grain, a mill turns it to flour, a bakery bakes it into bread (Food).
    // Nothing but the food buildings ever touches Grain/Flour, so a match without
    // them leaves those two columns of every stockpile at zero.
    public enum ResourceType { Wood = 0, Stone = 1, Food = 2, Grain = 3, Flour = 4 }

    // What a unit is currently doing beyond just moving/fighting.
    // Gathering = a worker sent to a node BY HAND. Working = a peasant bound to a
    // harvesting work building (hut, quarry, farm), which finds it a fresh node of
    // the right kind whenever it runs out — the self-running economy. Manning = a
    // peasant staffing a workshop (mill, bakery): it stands at the building and the
    // building converts goods only while manned. Gathering and Working share the
    // walk/harvest/haul cycle; Manning does not (nothing to haul).
    public enum Job { None = 0, Gathering = 1, Working = 2, Manning = 3 }

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
        public const int Count = 5;   // Wood, Stone, Food, Grain, Flour
    }

    // A unit blueprint: the stats every unit built from it inherits. This is the
    // point-buy mechanic — instead of one hardcoded soldier, players compose a
    // roster of designs, each spending a fixed POINT BUDGET across stats. A
    // glass cannon and a walking tank can cost the same points, spent differently.
    //
    // Stats are stored as small integers and converted to fixed-point on demand,
    // so a design is trivially serialisable and hashable. The default soldier
    // (registered as design 0) reproduces the pre-point-buy numbers exactly, so
    // the parity constant and every existing test are unaffected.
    public sealed class UnitDesign
    {
        public int Hp;          // hit points
        public int Damage;      // average blow; a hit rolls NextInt(Damage-2, Damage+3)
        public int SpeedStat;   // 5 == the classic 1/8-tile-per-tick; speed = One*Stat/40
        public int RangeStat;   // reach in half-tiles; 3 == 1.5 tiles; range = One*Stat/2
        public int Cooldown;    // ticks between blows

        public int SpeedFixed => Fixed.One * SpeedStat / 40;
        public int RangeFixed => Fixed.One * RangeStat / 2;

        // What this design spends of the budget. A defensible, tunable weighting:
        // hp is cheap per point, damage and a short cooldown are dear.
        public int PointCost =>
            Hp / 10 + Damage * 2 + SpeedStat + RangeStat + Max0(15 - Cooldown);

        static int Max0(int v) => v > 0 ? v : 0;

        public UnitDesign Clone() => new UnitDesign
        {
            Hp = Hp, Damage = Damage, SpeedStat = SpeedStat, RangeStat = RangeStat, Cooldown = Cooldown,
        };

        // The classic soldier, and the ceiling every custom design is measured
        // against — its cost is the budget, so a design may spend up to what the
        // baseline soldier does, allocated however the player likes.
        public static UnitDesign DefaultSoldier() => new UnitDesign
        {
            Hp = 100, Damage = 10, SpeedStat = 5, RangeStat = 3, Cooldown = 10,
        };
    }

    // A placed structure. Its footprint (X,Y top-left, W×H tiles) blocks the map
    // while it stands. Barracks carry a small production queue; a Keep anchors its
    // owner's drop-off. Position is whole tiles, never fixed-point.
    public sealed class Building
    {
        public int Id;
        public int Owner;
        public BuildingType Type;
        public int X, Y, W, H;
        public int Hp, MaxHp;

        // Production: the design ids queued to build (FIFO), and ticks left on the
        // one at the front. Only Barracks use these.
        public List<int> TrainQueue = new();
        public int BuildTimer;

        // A gatehouse's gate. Open lets units cross its tile; closed blocks it
        // like a wall. Ignored by every other building type.
        public bool Open;

        // A woodcutter's hut owns one woodcutter (WorkerId). The hut assigns it
        // trees; if the worker dies the hut breeds a new one. Zero for every other
        // building type.
        public int WorkerId;

        public bool Alive => Hp > 0;
        public int CenterX => X + W / 2;
        public int CenterY => Y + H / 2;

        public Building Clone() => new Building
        {
            Id = Id, Owner = Owner, Type = Type, X = X, Y = Y, W = W, H = H,
            Hp = Hp, MaxHp = MaxHp, TrainQueue = new List<int>(TrainQueue),
            BuildTimer = BuildTimer, Open = Open, WorkerId = WorkerId,
        };
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
        public int DesignId;  // which UnitDesign this unit was built from
        public int X, Y;      // fixed-point position
        public int Tx, Ty;    // fixed-point position of the CURRENT waypoint
        public int Hp;
        public int MaxHp;

        // Combat. TargetId is the enemy UNIT this is engaging; TargetBuildingId is
        // an enemy BUILDING being besieged. At most one is non-zero — issuing one
        // target clears the other. A unit only fights once given an order, which
        // keeps Move-only scenarios (the parity test) entirely out of combat.
        // AttackTimer counts ticks until the next blow may land.
        public int TargetId;
        public int TargetBuildingId;
        public int AttackTimer;

        // Economy. A unit gathering carries up to a full load from a node back to
        // its owner's drop-off, then repeats. GatherNodeId is the assignment;
        // CarryType/CarryAmount is what it is hauling right now.
        public Job Job;
        public int GatherNodeId;
        public ResourceType CarryType;
        public int CarryAmount;
        public int GatherTimer;

        // A peasant is population, not a soldier: it is what STAFFS a work
        // building (see ResolveWorkBuildings) and what food breeds more of (see
        // ResolvePopulation). The flag rides with the unit so an idle peasant
        // waiting for a job still reads as a peasant, not a soldier. It is NOT in
        // the frozen Checksum (units-only, and peasants never appear in the parity
        // scenario) — only in StateChecksum, like every other post-freeze field.
        public bool IsPeasant;

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
                Id = Id, Owner = Owner, DesignId = DesignId, X = X, Y = Y, Tx = Tx, Ty = Ty,
                Hp = Hp, MaxHp = MaxHp, TargetId = TargetId,
                TargetBuildingId = TargetBuildingId, AttackTimer = AttackTimer,
                Job = Job, GatherNodeId = GatherNodeId, CarryType = CarryType,
                CarryAmount = CarryAmount, GatherTimer = GatherTimer, IsPeasant = IsPeasant,
                PathIndex = PathIndex,
            };
            if (Path != null) copy.Path = new List<Tile>(Path);
            return copy;
        }
    }

    // A blow that landed this tick, from an attacker to what it hit. Transient
    // render candy — the renderer turns a long-range one into a flying
    // projectile. NOT game state: cleared every tick, never hashed, never
    // snapshotted, never read back by the sim, so it cannot affect determinism.
    public struct Shot
    {
        public int FromX, FromY, ToX, ToY;   // fixed-point
    }

    public sealed class Simulation
    {
        public int TickNumber;
        public readonly List<Unit> Units = new(); // always iterated in id order

        // Blows that landed this tick (see Shot). Working memory for rendering,
        // not part of the simulation's state.
        public readonly List<Shot> ShotsThisTick = new();
        public readonly List<ResourceNode> Nodes = new(); // id order
        public readonly List<Building> Buildings = new(); // id order
        public readonly TileMap Map;
        int _nextId = 1;
        int _nextNodeId = 1;
        int _nextBuildingId = 1;

        // Per-owner stockpiles and drop-off points. SortedDictionary so every
        // machine hashes owners in the same order — a plain Dictionary iterates in
        // insertion order, which two machines could reach differently.
        readonly SortedDictionary<int, int[]> _stock = new();
        readonly SortedDictionary<int, Tile> _dropOff = new();

        readonly int _arriveEps = Fixed.One / 4;

        // --- Unit designs (point-buy) -----------------------------------------
        // Per-unit speed, damage, reach and cooldown now come from the unit's
        // design rather than a shared constant. Design 0 is the default soldier,
        // registered in the constructor, and reproduces the old constants exactly.
        public const int MaxDesignPoints = 43;   // == the default soldier's cost
        readonly List<UnitDesign> _designs = new();

        // --- Combat tuning that is NOT per-design ------------------------------
        static readonly int AggroRange = Fixed.FromInt(7);      // acquire the next foe within this
        const int ChaseRepathEvery = 6;                         // ticks between chase re-paths

        // --- Economy tuning ---------------------------------------------------
        static readonly int GatherRange = Fixed.One * 3 / 2;    // reach to a node, 1.5 tiles
        // A drop-off point is always a reachable tile right beside its building
        // (SpawnPointAround / SetDropOff pick one), never the walled-in centre —
        // so the worker can walk RIGHT UP to it. Matching GatherRange means it
        // deposits only when it has arrived at the door, instead of dumping the
        // load from a few tiles out (which read as the peasant not bothering to
        // reach the keep).
        static readonly int DropOffRange = Fixed.One * 3 / 2;   // 1.5 tiles
        const int CarryCapacity = 10;                           // load a worker hauls
        const int GatherInterval = 5;                           // ticks per 1 unit gathered

        // --- Work buildings (the self-running economy) ------------------------
        // How far from itself a work building will send its worker for a node.
        // Beyond this it sits idle — so you place a hut IN the forest and a quarry
        // ON the stone, which is the point.
        const int WorkRange = 18;                               // tiles

        // --- The food chain ---------------------------------------------------
        // A farm plants a wheat field beside itself; its farmer harvests and hauls
        // grain like any gatherer. When the field is used up the farm plants a
        // fresh one, so a farm is a renewable grain source, not a finite deposit.
        const int FieldGrain = 240;                             // grain in one planted field
        // The two workshops. Each turns a batch of its input good into a batch of
        // output every interval, but ONLY when the input is on hand — the timer
        // arms and waits, so a mill with no grain simply idles until grain arrives.
        // Balanced so the chain roughly keeps pace: a farm feeds a mill feeds a
        // bakery, and bread is the richest step (a loaf feeds several soldiers).
        const int MillInterval = 25;                            // ticks per batch (1.25s)
        const int MillInput = 4, MillOutput = 4;                // grain -> flour, 1:1
        const int BakeryInterval = 25;
        const int BakeryInput = 4, BakeryOutput = 6;            // flour -> bread, generous

        // --- Population -------------------------------------------------------
        // Peasants are the workforce, and food is what raises them. Every so often
        // a keep that can feed a new mouth breeds a peasant; that peasant then goes
        // and staffs whatever work building is short a worker. This closes the
        // loop: food -> peasants -> they work the farms/mills/bakeries -> more food.
        const int PopInterval = 30;                             // ticks between births (1.5s)
        const int PopFoodCost = 12;                             // food eaten to raise one peasant

        // A mill or bakery is a WORKSHOP: it needs a peasant standing in it to run,
        // but unlike a harvester that peasant hauls nothing — it just mans the
        // place. A harvester (hut/quarry/farm) is any building with a WorkResource.
        static bool IsWorkshop(BuildingType t) => t == BuildingType.Mill || t == BuildingType.Bakery;
        static bool NeedsWorker(BuildingType t) => WorkResource(t) != null || IsWorkshop(t);
        // How close the miller/baker must be for the workshop to actually run.
        static readonly int ManningRange = Fixed.One * 2;      // 2 tiles

        // What each work building harvests. A building type not listed here is not
        // a work building and grows no worker.
        static ResourceType? WorkResource(BuildingType t) => t switch
        {
            BuildingType.WoodcutterHut => ResourceType.Wood,
            BuildingType.Quarry => ResourceType.Stone,
            // A farm is a work building like any other — its farmer harvests the
            // wheat field the farm plants for itself (see PlantField) and hauls
            // the grain home, reusing the whole gather/haul cycle. The mill and
            // bakery are NOT work buildings: they are workshops that transform one
            // stockpile good into another (see ResolveProcessors), no worker.
            BuildingType.Farm => ResourceType.Grain,
            _ => (ResourceType?)null,
        };

        // --- Buildings --------------------------------------------------------
        const int TrainTime = 60;                               // ticks to produce one unit (3s)
        const int TrainCostWood = 15;                           // per unit trained at a Barracks
                                                                // (flat: the point budget balances power)

        // Footprint size and placement cost per building type, indexed by
        // (int)BuildingType. Cost is [wood, stone, food]. Walls and gatehouses
        // are 1x1 so a player lays them out tile by tile into a curtain wall.
        static readonly int[] FootW = { 3, 2, 1, 1, 2, 2, 2, 3, 2, 2 };  // ...Farm, Mill, Bakery
        static readonly int[] FootH = { 3, 2, 1, 1, 2, 2, 2, 3, 2, 2 };
        static readonly int[][] BuildCost =
        {
            new[] { 30, 20, 0 },   // Keep
            new[] { 40, 0, 0 },    // Barracks
            new[] { 0, 5, 0 },     // Wall — cheap stone, meant to be spammed
            new[] { 10, 10, 0 },   // Gatehouse
            new[] { 15, 0, 0 },    // Woodcutter's Hut — cheap, so the wood economy bootstraps
            new[] { 20, 5, 0 },    // Storehouse — a drop-off closer to the trees
            new[] { 20, 0, 0 },    // Quarry — built from wood, then it pays back in stone
            new[] { 15, 0, 0 },    // Farm — cheap; the field feeds the whole chain
            new[] { 20, 15, 0 },   // Mill — a stone workshop that grinds grain to flour
            new[] { 25, 15, 0 },   // Bakery — turns flour into bread (Food)
        };
        // Structural hit points per type. A wall is tough enough to buy time but
        // not permanent — a handful of soldiers breach it in well under a minute.
        static readonly int[] BuildHp = { 600, 250, 200, 250, 180, 220, 200, 150, 220, 220 };

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
            _designs.Add(UnitDesign.DefaultSoldier());   // design 0, always present
            Fog = new Vision(Map);
        }

        // ---- Fog of war -------------------------------------------------------
        // Per-player sight. `Fog.Explored` is accumulated game state (hashed,
        // snapshotted); `Fog.IsVisible` is derived from current positions and is
        // rebuilt at the top of every Tick. See Vision.cs.
        //
        // FogEnabled exists because fog CHANGES WHAT ORDERS ARE LEGAL, and a
        // great deal of this project's verification — the parity scenario, the
        // combat and economy suites — was written against a sim with no fog, in
        // scenarios that place units far apart and order them at each other
        // immediately. Turning fog on globally would silently rewrite what those
        // tests are testing. So it is opt-in, exactly like every other feature
        // that can change a checksum: off, the simulation behaves precisely as it
        // did before, and 0xB1A7A676 is untouched.
        public Vision Fog;
        public bool FogEnabled;

        // Can this player act on that spot at all? With fog off, everything is
        // both seen and known — which is what keeps every pre-fog scenario intact.
        public bool CanSee(int owner, int x, int y) => !FogEnabled || Fog.IsVisible(owner, x, y);
        public bool HasExplored(int owner, int x, int y) => !FogEnabled || Fog.IsExplored(owner, x, y);
        public bool CanSeeUnit(int owner, Unit u) => CanSee(owner, Fixed.ToInt(u.X), Fixed.ToInt(u.Y));

        // A building is bigger than a tile and does not move: knowing where a
        // wall stands is knowledge you keep. So a structure counts as known once
        // ANY of its footprint has been explored — you may besiege a keep you
        // scouted an hour ago, which is how the genre has always worked.
        public bool HasExploredBuilding(int owner, Building b)
        {
            if (!FogEnabled) return true;
            for (int y = b.Y; y < b.Y + b.H; y++)
                for (int x = b.X; x < b.X + b.W; x++)
                    if (Fog.IsExplored(owner, x, y)) return true;
            return false;
        }

        // ---- Unit designs (point-buy) ----------------------------------------

        public IReadOnlyList<UnitDesign> Designs => _designs;
        public UnitDesign DesignOf(int designId) =>
            designId >= 0 && designId < _designs.Count ? _designs[designId] : _designs[0];

        // Register a custom design and return its id, or -1 if it busts the point
        // budget. For match setup — call it identically on every machine before the
        // match runs, like SpawnUnit. Designs don't change once the match is live.
        public int RegisterDesign(UnitDesign design)
        {
            if (design == null || design.PointCost > MaxDesignPoints) return -1;
            _designs.Add(design.Clone());
            return _designs.Count - 1;
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
                            IReadOnlyDictionary<int, int[]> stock, IReadOnlyDictionary<int, Tile> dropOff,
                            int nextBuildingId, IReadOnlyList<Building> buildings,
                            IReadOnlyList<UnitDesign> designs,
                            bool fogEnabled = false, IReadOnlyDictionary<int, uint[]> explored = null)
        {
            TickNumber = tickNumber;
            _nextId = nextUnitId;
            _nextNodeId = nextNodeId;
            _nextBuildingId = nextBuildingId;
            _rng.Restore(rngState);

            _designs.Clear();
            foreach (var d in designs) _designs.Add(d.Clone());

            Units.Clear();
            foreach (var u in units) Units.Add(u.Clone());

            Nodes.Clear();
            foreach (var n in nodes) Nodes.Add(n.Clone());

            _stock.Clear();
            foreach (var kv in stock) _stock[kv.Key] = (int[])kv.Value.Clone();

            _dropOff.Clear();
            foreach (var kv in dropOff) _dropOff[kv.Key] = kv.Value;

            // Rebuild the buildings AND the map occupancy they imply — the
            // rejoiner's map starts as bare terrain, so the footprints have to be
            // re-stamped or its pathfinding would route straight through walls
            // that the host's does not.
            Buildings.Clear();
            Map.ClearBlocked();
            foreach (var b in buildings)
            {
                var copy = b.Clone();
                Buildings.Add(copy);
                // Re-block, EXCEPT an open gate, whose tile stays walkable — get
                // this wrong and the rejoiner's pathfinder would treat an open
                // gateway as a solid wall.
                BlockFootprint(copy, !(copy.Type == BuildingType.Gatehouse && copy.Open));
            }

            // Fog: adopt what the sender had EXPLORED, then rebuild what is
            // currently VISIBLE from the units we were just handed. Copying the
            // visible set instead would be the same mistake as copying the
            // footprint blocking — carrying over derived state that the local
            // world should be deriving for itself.
            FogEnabled = fogEnabled;
            Fog.RestoreExplored(explored);
            if (FogEnabled) Fog.RecomputeVisible(Units, Buildings);
        }

        // Restore straight from a snapshot object — the same unpacking every
        // caller was doing by hand.
        public void Restore(MatchSnapshot s) =>
            Restore(s.Tick, s.NextUnitId, s.RngState, s.Units, s.NextNodeId, s.Nodes,
                    s.Stock, s.DropOffs, s.NextBuildingId, s.Buildings, s.Designs,
                    s.FogEnabled, s.Explored);

        // A complete, standalone snapshot of the simulation's state right now — no
        // network bookkeeping (no pending turns). This is what a rejoin adopts and
        // what a replay records as its starting point.
        public MatchSnapshot Snapshot()
        {
            var units = new Unit[Units.Count];
            for (int i = 0; i < units.Length; i++) units[i] = Units[i].Clone();
            var nodes = new ResourceNode[Nodes.Count];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = Nodes[i].Clone();
            var buildings = new Building[Buildings.Count];
            for (int i = 0; i < buildings.Length; i++) buildings[i] = Buildings[i].Clone();
            var designs = new UnitDesign[_designs.Count];
            for (int i = 0; i < designs.Length; i++) designs[i] = _designs[i].Clone();

            var stock = new Dictionary<int, int[]>();
            foreach (var kv in _stock) stock[kv.Key] = (int[])kv.Value.Clone();
            var drops = new Dictionary<int, Tile>();
            foreach (var kv in _dropOff) drops[kv.Key] = kv.Value;

            return new MatchSnapshot
            {
                Tick = TickNumber,
                NextUnitId = _nextId,
                NextNodeId = _nextNodeId,
                NextBuildingId = _nextBuildingId,
                RngState = _rng.State,
                Units = units,
                Nodes = nodes,
                Buildings = buildings,
                Designs = designs,
                Stock = stock,
                DropOffs = drops,
                FogEnabled = FogEnabled,
                Explored = Fog.CopyExplored(),
                Checksum = StateChecksum(),
            };
        }

        // Read-only views for snapshotting. Sorted iteration is preserved, so a
        // snapshot serialises owners in a fixed order on every machine.
        public IReadOnlyList<ResourceNode> NodeList => Nodes;
        public IReadOnlyList<Building> BuildingList => Buildings;
        public IReadOnlyList<UnitDesign> DesignList => _designs;
        public IReadOnlyDictionary<int, int[]> Stockpiles => _stock;
        public IReadOnlyDictionary<int, Tile> DropOffs => _dropOff;

        public Unit SpawnUnit(int owner, int xInt, int yInt) => SpawnUnit(owner, xInt, yInt, 0);

        public Unit SpawnUnit(int owner, int xInt, int yInt, int designId)
        {
            var d = DesignOf(designId);
            var u = new Unit
            {
                Id = _nextId++,
                Owner = owner,
                DesignId = designId >= 0 && designId < _designs.Count ? designId : 0,
                X = Fixed.FromInt(xInt),
                Y = Fixed.FromInt(yInt),
                Tx = Fixed.FromInt(xInt),
                Ty = Fixed.FromInt(yInt),
                Hp = d.Hp,
                MaxHp = d.Hp,
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

        // Grant resources directly. For match setup (starting stockpiles) — call
        // it identically on every machine before the match runs, exactly like
        // SpawnUnit. Not an in-match action: there is no command for it.
        public void AddResource(int owner, ResourceType type, int amount) =>
            StockOf(owner)[(int)type] += amount;

        public int NextNodeId => _nextNodeId;

        int[] StockOf(int owner)
        {
            if (!_stock.TryGetValue(owner, out var s)) { s = new int[Resources.Count]; _stock[owner] = s; }
            return s;
        }

        public int NextBuildingId => _nextBuildingId;

        // Can a building of this type legally sit with its top-left at (x,y)?
        // Every footprint tile must be in bounds, passable terrain, and free of
        // any other building. Uses the SAME passability the pathfinder does, so a
        // building is never placed where a unit could not have stood.
        public bool CanPlace(BuildingType type, int x, int y)
        {
            int w = FootW[(int)type], h = FootH[(int)type];
            for (int ty = y; ty < y + h; ty++)
                for (int tx = x; tx < x + w; tx++)
                    if (!Map.Passable(tx, ty)) return false;   // out of bounds, terrain, or already blocked
            return true;
        }

        // Place a building directly, no cost, no validation beyond fit. For match
        // setup and tests — the Build COMMAND (which charges and validates) is the
        // in-game path. Returns null if it will not fit.
        public Building PlaceBuilding(BuildingType type, int owner, int x, int y)
        {
            if (!CanPlace(type, x, y)) return null;

            var b = new Building
            {
                Id = _nextBuildingId++, Owner = owner, Type = type,
                X = x, Y = y, W = FootW[(int)type], H = FootH[(int)type],
                Hp = BuildHp[(int)type], MaxHp = BuildHp[(int)type],
            };
            Buildings.Add(b);
            BlockFootprint(b, true);

            // Building over resources clears them — the trees you raised your hut
            // on are gone, not buried under it where no worker could ever reach
            // them. (That buried-tree case is exactly what froze the first
            // woodcutter: its nearest tree sat under its own hut.)
            Nodes.RemoveAll(n => n.X >= x && n.X < x + b.W && n.Y >= y && n.Y < y + b.H);

            // And shove any UNIT out of the footprint. A building blocks its
            // tiles, and a unit left standing on a blocked tile can path nowhere —
            // it is trapped forever. This is exactly what stranded a woodcutter
            // when a storehouse was dropped on top of it.
            EvictUnitsFrom(b);

            // A keep is where its owner's gatherers deposit — at a REACHABLE tile
            // just outside its footprint, not the walled-in centre (which no
            // worker could path to or stand on).
            if (type == BuildingType.Keep)
            {
                var drop = SpawnPointAround(b) ?? new Tile(b.CenterX, b.CenterY);
                SetDropOff(owner, drop.X, drop.Y);
            }

            // A work building does NOT come with a worker any more: peasants are
            // population, and a building stands idle until one is free to staff it
            // (see ResolveWorkBuildings). Food breeds that population — this is the
            // loop that makes food matter.

            // A farm still sows its field at once, so grain is standing the moment
            // a farmer is assigned rather than a tick later.
            if (type == BuildingType.Farm) PlantField(b);

            return b;
        }

        // Move any unit standing inside a building's footprint to the nearest
        // free tile just outside it, and stop it where it lands. Deterministic:
        // units in id order, tiles scanned in a fixed spiral, so every machine
        // relocates each unit to the same tile.
        void EvictUnitsFrom(Building b)
        {
            foreach (var u in Units)                // id order
            {
                int ux = Fixed.ToInt(u.X), uy = Fixed.ToInt(u.Y);
                if (ux < b.X || ux >= b.X + b.W || uy < b.Y || uy >= b.Y + b.H) continue;

                var spot = NearestFreeTile(b.CenterX, b.CenterY) ?? SpawnPointAround(b);
                if (spot.HasValue)
                {
                    u.X = Fixed.FromInt(spot.Value.X); u.Y = Fixed.FromInt(spot.Value.Y);
                    u.Tx = u.X; u.Ty = u.Y;
                    u.Path = null; u.PathIndex = 0;   // its old route started inside the wall; drop it
                }
            }
        }

        // Nearest passable tile to (cx,cy), searched in growing rings. Ties broken
        // by the fixed scan order, so it is the same on every machine.
        Tile? NearestFreeTile(int cx, int cy)
        {
            for (int r = 1; r <= 6; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;   // ring edge only
                        if (Map.Passable(cx + dx, cy + dy)) return new Tile(cx + dx, cy + dy);
                    }
            return null;
        }

        void BlockFootprint(Building b, bool blocked)
        {
            for (int ty = b.Y; ty < b.Y + b.H; ty++)
                for (int tx = b.X; tx < b.X + b.W; tx++)
                    Map.SetBlocked(tx, ty, blocked);
        }

        public IReadOnlyList<int> CostOf(BuildingType type) => BuildCost[(int)type];

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
                    // And only one you can actually SEE. This is the rule that
                    // makes fog worth having: without it a client could order a
                    // strike on a unit hidden behind the ridge, which is exactly
                    // the maphack the whole feature exists to prevent.
                    if (!CanSeeUnit(cmd.Owner, target)) break;
                    foreach (var id in cmd.UnitIds)
                    {
                        var u = Units.Find(v => v.Id == id);
                        if (u != null && u.Owner == cmd.Owner && u.Id != target.Id)
                        {
                            u.Job = Job.None;         // stop gathering to go fight
                            u.TargetBuildingId = 0;   // a unit target replaces a siege target
                            u.TargetId = target.Id;   // the combat phase does the chasing/hitting
                        }
                    }
                    break;

                case CommandType.AttackBuilding:
                    // TargetId carries the building id. Only an ENEMY building can
                    // be besieged; your own (and a bad id) is ignored.
                    var wall = Buildings.Find(x => x.Id == cmd.TargetId);
                    if (wall == null || !wall.Alive || wall.Owner == cmd.Owner) break;
                    // Explored, not visible: a structure you have scouted stays
                    // on your map, and marching on a keep you saw an hour ago is
                    // the whole point of scouting.
                    if (!HasExploredBuilding(cmd.Owner, wall)) break;
                    foreach (var id in cmd.UnitIds)
                    {
                        var u = Units.Find(v => v.Id == id);
                        if (u != null && u.Owner == cmd.Owner)
                        {
                            u.Job = Job.None;
                            u.TargetId = 0;
                            u.TargetBuildingId = wall.Id;
                        }
                    }
                    break;

                case CommandType.Gather:
                    // TargetId carries the node id for a Gather order. A worker can
                    // only be sent to a node the owner has a drop-off for, or the
                    // load it hauls would have nowhere to go.
                    var node = Nodes.Find(n => n.Id == cmd.TargetId);
                    if (node == null || !_dropOff.ContainsKey(cmd.Owner)) break;
                    // Explored is the right test again: a wood you have found is
                    // a wood you can keep working, whether or not anyone of yours
                    // is standing there this instant.
                    if (!HasExplored(cmd.Owner, node.X, node.Y)) break;
                    foreach (var id in cmd.UnitIds)
                    {
                        var u = Units.Find(v => v.Id == id);
                        if (u != null && u.Owner == cmd.Owner)
                        {
                            u.TargetId = 0;           // stop fighting to go work
                            u.TargetBuildingId = 0;
                            u.Job = Job.Gathering;
                            u.GatherNodeId = node.Id;
                            u.GatherTimer = 0;
                        }
                    }
                    break;

                case CommandType.Build:
                    // TargetId carries the building type; X,Y the top-left tile.
                    // Refused silently if it will not fit or the owner cannot pay —
                    // a refused build simply spends nothing and places nothing.
                    var type = (BuildingType)cmd.TargetId;
                    if ((int)type < 0 || (int)type >= BuildCost.Length) break;
                    if (!CanPlace(type, cmd.X, cmd.Y) || !CanAfford(cmd.Owner, BuildCost[(int)type])) break;
                    // No building on ground you have never laid eyes on. Checked
                    // over the whole footprint, so a keep cannot be half-planted
                    // in the dark.
                    if (!ExploredFootprint(cmd.Owner, type, cmd.X, cmd.Y)) break;
                    Pay(cmd.Owner, BuildCost[(int)type]);
                    PlaceBuilding(type, cmd.Owner, cmd.X, cmd.Y);
                    break;

                case CommandType.Train:
                    // TargetId carries the barracks id; X the design to build.
                    // Costs wood and queues that design; the production phase
                    // spawns it when the timer elapses.
                    var barracks = Buildings.Find(x => x.Id == cmd.TargetId);
                    if (barracks == null || barracks.Owner != cmd.Owner ||
                        barracks.Type != BuildingType.Barracks) break;
                    int designId = cmd.X >= 0 && cmd.X < _designs.Count ? cmd.X : 0;
                    var trainCost = new[] { TrainCostWood, 0, 0 };
                    if (!CanAfford(cmd.Owner, trainCost)) break;
                    Pay(cmd.Owner, trainCost);
                    barracks.TrainQueue.Add(designId);
                    break;

                case CommandType.ToggleGate:
                    // TargetId carries the gatehouse id. Flipping the gate flips
                    // its tile's passability: an open gate is walkable, a closed
                    // one blocks like a wall.
                    var gate = Buildings.Find(x => x.Id == cmd.TargetId);
                    if (gate == null || gate.Owner != cmd.Owner ||
                        gate.Type != BuildingType.Gatehouse) break;
                    gate.Open = !gate.Open;
                    BlockFootprint(gate, !gate.Open);
                    break;
            }
        }

        bool ExploredFootprint(int owner, BuildingType type, int x, int y)
        {
            if (!FogEnabled) return true;
            for (int ty = y; ty < y + FootH[(int)type]; ty++)
                for (int tx = x; tx < x + FootW[(int)type]; tx++)
                    if (!Fog.IsExplored(owner, tx, ty)) return false;
            return true;
        }

        // A cost lists only the resources it charges (wood/stone/food); it never
        // mentions the food-chain intermediates, so iterate the COST's length, not
        // Resources.Count — a 3-long cost against a 5-wide stockpile must not read
        // past its end.
        bool CanAfford(int owner, IReadOnlyList<int> cost)
        {
            for (int i = 0; i < cost.Count; i++)
                if (Stockpile(owner, (ResourceType)i) < cost[i]) return false;
            return true;
        }

        void Pay(int owner, IReadOnlyList<int> cost)
        {
            var s = StockOf(owner);
            for (int i = 0; i < cost.Count; i++) s[i] -= cost[i];
        }

        // Cancel whatever task a unit was on. Called before a plain Move so an
        // order to reposition always wins over a standing job.
        static void StopWork(Unit u)
        {
            u.TargetId = 0;
            u.TargetBuildingId = 0;
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
            ShotsThisTick.Clear();   // transient render log; nothing here is game state

            // Sight FIRST, so a command is judged against the world as the player
            // could last have seen it — and at the same point in the sequence on
            // every machine. Doing it after the commands would mean an order was
            // legal or not depending on where the move it triggered ended up.
            if (FogEnabled) Fog.Update(Units, Buildings);

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
                    int speed = DesignOf(u.DesignId).SpeedFixed;   // per-unit, from its design
                    int step = dist < speed ? dist : speed;
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
            RemoveDestroyedBuildings();
            ResolveWorkBuildings(); // hand idle peasants their next node...
            ResolveEconomy();       // ...before the shared walk/harvest/haul cycle runs
            ResolveProduction();
            ResolveProcessors();    // mills/bakeries turn last tick's harvest into food
            ResolvePopulation();    // and food, in turn, raises the next peasant
            TickNumber++;
        }

        // Barracks turn their queue into units. Iterated in id order so a spawn
        // (and the id it takes) happens in the same sequence on every machine.
        void ResolveProduction()
        {
            foreach (var b in Buildings)
            {
                if (b.Type != BuildingType.Barracks || b.TrainQueue.Count == 0) continue;

                if (b.BuildTimer <= 0) b.BuildTimer = TrainTime;
                b.BuildTimer--;

                if (b.BuildTimer <= 0)
                {
                    b.BuildTimer = 0;
                    var spot = SpawnPointAround(b);
                    // No free tile this tick: leave the unit queued and try again
                    // next tick, rather than dropping it.
                    if (spot.HasValue)
                    {
                        int designId = b.TrainQueue[0];
                        b.TrainQueue.RemoveAt(0);
                        SpawnUnit(b.Owner, spot.Value.X, spot.Value.Y, designId);
                    }
                }
            }
        }

        // First passable tile on the ring just outside a building's footprint,
        // scanned in a fixed order so both machines pick the same one.
        Tile? SpawnPointAround(Building b)
        {
            for (int tx = b.X - 1; tx <= b.X + b.W; tx++)
            {
                if (Map.Passable(tx, b.Y - 1)) return new Tile(tx, b.Y - 1);
                if (Map.Passable(tx, b.Y + b.H)) return new Tile(tx, b.Y + b.H);
            }
            for (int ty = b.Y; ty < b.Y + b.H; ty++)
            {
                if (Map.Passable(b.X - 1, ty)) return new Tile(b.X - 1, ty);
                if (Map.Passable(b.X + b.W, ty)) return new Tile(b.X + b.W, ty);
            }
            return null;
        }

        // The gathering loop, iterated in id order (no RNG, pure integer state).
        // A worker cycles: walk to its node, gather to a full load, walk to the
        // NEAREST drop-off, deposit, repeat. Runs for both hand-assigned gatherers
        // (Job.Gathering) and hut-bound woodcutters (Job.Woodcutting) — the cycle
        // is identical; only what happens when the node runs out differs. A match
        // with neither job (the parity scenario) is untouched.
        void ResolveEconomy()
        {
            foreach (var u in Units)
            {
                if (u.Job != Job.Gathering && u.Job != Job.Working) continue;

                var node = Nodes.Find(n => n.Id == u.GatherNodeId);
                bool nodeGone = node == null || node.Amount <= 0;
                bool full = u.CarryAmount >= CarryCapacity;

                if (full || (nodeGone && u.CarryAmount > 0))
                {
                    // Haul the load to the closest drop-off — a keep or a
                    // storehouse. If the owner has none (all razed mid-haul), there
                    // is nowhere to bank, so stand down.
                    if (!NearestDropOff(u.Owner, u.X, u.Y, out var drop)) { EndJob(u); continue; }
                    if (WithinRange(u, drop.X, drop.Y, DropOffRange))
                    {
                        StockOf(u.Owner)[(int)u.CarryType] += u.CarryAmount;
                        u.CarryAmount = 0;
                        if (nodeGone) FinishNode(u);          // tree/node exhausted
                        else Order(u, node.X, node.Y);        // back for another load
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
                    FinishNode(u);   // node gone and empty-handed
                }
            }

            Nodes.RemoveAll(n => n.Amount <= 0);
        }

        // A worker's node ran out. A hand-assigned gatherer stands down; a work
        // building's peasant just clears its assignment and waits —
        // ResolveWorkBuildings hands it the next node, so the building works on.
        void FinishNode(Unit u)
        {
            if (u.Job == Job.Working) { u.GatherNodeId = 0; u.GatherTimer = 0; }
            else EndJob(u);
        }

        // The nearest place owner can deposit goods: their keep's drop-off tile,
        // or a tile beside any storehouse they own — whichever is closest to
        // (fx,fy). Iterated in id order with a strict compare, so every machine
        // picks the same one.
        bool NearestDropOff(int owner, int fx, int fy, out Tile best)
        {
            best = default;
            long bestD = long.MaxValue;
            bool found = false;

            if (_dropOff.TryGetValue(owner, out var keep))
            {
                bestD = DropDist(keep.X, keep.Y, fx, fy); best = keep; found = true;
            }
            foreach (var b in Buildings)               // id order
            {
                if (b.Type != BuildingType.Storehouse || b.Owner != owner || !b.Alive) continue;
                var t = SpawnPointAround(b) ?? new Tile(b.CenterX, b.CenterY);
                long d = DropDist(t.X, t.Y, fx, fy);
                if (d < bestD) { bestD = d; best = t; found = true; }
            }
            return found;
        }

        static long DropDist(int tx, int ty, int fx, int fy)
        {
            long dx = Fixed.FromInt(tx) - fx, dy = Fixed.FromInt(ty) - fy;
            return dx * dx + dy * dy;
        }

        // ---- Work buildings: staffed from population ---------------------------
        // A work building runs itself, but it needs a PEASANT to run: a building
        // short a worker hires the nearest idle peasant of its owner. A harvester
        // (hut/quarry/farm) then works the gather/haul cycle; a workshop (mill,
        // bakery) just keeps its peasant standing inside, and only produces while
        // it is manned. A building with no peasant free to hire simply waits — that
        // waiting is the whole point: population, fed by food, is the real limit on
        // how much economy you can run at once. Runs BEFORE the gather cycle so a
        // freshly-hired harvester is handed its node the same tick.
        void ResolveWorkBuildings()
        {
            foreach (var wb in Buildings)             // id order
            {
                if (!wb.Alive || !NeedsWorker(wb.Type)) continue;

                // A farm keeps a wheat field standing beside it: if the last one has
                // been cut down to nothing, sow a fresh one. This is what makes the
                // farm renewable — its farmer never runs out of grain to reap.
                if (wb.Type == BuildingType.Farm && NearestResource(wb, ResourceType.Grain) == null)
                    PlantField(wb);

                var worker = wb.WorkerId != 0 ? Units.Find(u => u.Id == wb.WorkerId) : null;
                if (worker == null || !worker.Alive)
                {
                    // Vacancy: take on the nearest idle peasant, if the owner has
                    // one spare. Otherwise the building stands empty until one is.
                    wb.WorkerId = 0;
                    worker = HireIdlePeasant(wb);
                    if (worker == null) continue;
                    worker.Job = IsWorkshop(wb.Type) ? Job.Manning : Job.Working;
                    worker.GatherNodeId = 0;
                    worker.GatherTimer = 0;
                    wb.WorkerId = worker.Id;
                }

                if (IsWorkshop(wb.Type))
                {
                    // Keep the miller/baker at the workshop. Production waits for it
                    // to arrive — see Manned(), checked in ResolveProcessors. It
                    // walks to a tile BESIDE the building (the footprint itself is
                    // blocked, so ordering it onto the centre would path nowhere and
                    // strand it out of range forever).
                    if (DistToBuilding(worker, wb) > ManningRange)
                    {
                        var door = SpawnPointAround(wb) ?? new Tile(wb.CenterX, wb.CenterY);
                        ChaseTo(worker, door.X, door.Y);
                    }
                    else
                    {
                        worker.Path = null; worker.PathIndex = 0;
                        worker.Tx = worker.X; worker.Ty = worker.Y;
                    }
                }
                else if (worker.GatherNodeId == 0 && worker.CarryAmount == 0)
                {
                    // Harvester idle (no node, empty-handed): hand it the nearest
                    // standing node of its resource in reach.
                    var node = NearestResource(wb, WorkResource(wb.Type).Value);
                    if (node != null)
                    {
                        worker.Job = Job.Working;
                        worker.GatherNodeId = node.Id;
                        worker.GatherTimer = 0;
                    }
                }
            }
        }

        // The nearest idle peasant of a building's owner — a peasant with no job,
        // free to be put to work. Ties broken by unit id (id order + strict <).
        // Null if the owner has nobody spare, which is how a building goes unstaffed.
        Unit HireIdlePeasant(Building wb)
        {
            Unit best = null;
            long bestD = long.MaxValue;
            foreach (var u in Units)                  // id order
            {
                if (!u.IsPeasant || u.Owner != wb.Owner || !u.Alive || u.Job != Job.None) continue;
                long dx = (u.X >> 16) - wb.CenterX, dy = (u.Y >> 16) - wb.CenterY;
                long d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = u; }
            }
            return best;
        }

        // Raise a peasant at an owner's keep: population, not army — IsPeasant, no
        // job, waiting to be hired. Public so match setup can seed a starting
        // workforce, exactly like SpawnUnit. Spawns on passable ground by the keep.
        public Unit SpawnPeasant(int owner)
        {
            int x = 0, y = 0;
            if (_dropOff.TryGetValue(owner, out var d)) { x = d.X; y = d.Y; }
            var t = NearestFreeTile(x, y) ?? new Tile(x, y);
            var u = SpawnUnit(owner, t.X, t.Y, 0);
            u.IsPeasant = true;
            u.Job = Job.None;
            return u;
        }

        // Food raises peasants. On a slow tick, every keep that can spare the food
        // for another mouth breeds one. Iterated in building-id order (no RNG, pure
        // integer state); a side with no keep or no food breeds nobody.
        void ResolvePopulation()
        {
            if (TickNumber % PopInterval != 0) return;
            foreach (var b in Buildings)              // id order
            {
                if (b.Type != BuildingType.Keep || !b.Alive) continue;
                if (Stockpile(b.Owner, ResourceType.Food) < PopFoodCost) continue;
                StockOf(b.Owner)[(int)ResourceType.Food] -= PopFoodCost;
                SpawnPeasant(b.Owner);
            }
        }

        // Sow a farm's wheat field: one grain node on a passable tile just outside
        // the farm, so it is both reachable and in WorkRange. Nodes do not block a
        // tile, so the farmer stands on the field to reap it. Deterministic — the
        // tile comes from the same fixed-order ring scan as everything else.
        void PlantField(Building farm)
        {
            var spot = SpawnPointAround(farm);
            if (!spot.HasValue) return;               // hemmed in this tick; retry next
            Nodes.Add(new ResourceNode
            {
                Id = _nextNodeId++, Type = ResourceType.Grain,
                X = spot.Value.X, Y = spot.Value.Y, Amount = FieldGrain,
            });
        }

        // The workshops: a mill grinds grain into flour, a bakery bakes flour into
        // bread (Food). Both draw from and return to the owner's shared stockpile,
        // so they need not sit next to the farm — the grain the farmer banked at
        // the keep is the grain the mill grinds. Iterated in id order, pure integer
        // state, no RNG: a match with no such building is wholly untouched.
        void ResolveProcessors()
        {
            foreach (var b in Buildings)             // id order
            {
                if (!b.Alive || !Manned(b)) continue;   // an unstaffed workshop is idle
                if (b.Type == BuildingType.Mill)
                    Convert(b, ResourceType.Grain, MillInput, ResourceType.Flour, MillOutput, MillInterval);
                else if (b.Type == BuildingType.Bakery)
                    Convert(b, ResourceType.Flour, BakeryInput, ResourceType.Food, BakeryOutput, BakeryInterval);
            }
        }

        // A workshop runs only while its peasant is alive and standing in it.
        bool Manned(Building b)
        {
            if (b.WorkerId == 0) return false;
            var w = Units.Find(u => u.Id == b.WorkerId);
            return w != null && w.Alive && DistToBuilding(w, b) <= ManningRange;
        }

        // One workshop step. The timer counts up to the interval and then HOLDS
        // there until a full batch of input is available, so no production time is
        // lost while the workshop waits on its supplier. (BuildTimer is free for
        // these types — they are neither barracks nor work buildings.)
        void Convert(Building b, ResourceType inRes, int inAmt, ResourceType outRes, int outAmt, int interval)
        {
            if (b.BuildTimer < interval) b.BuildTimer++;
            if (b.BuildTimer < interval) return;
            if (Stockpile(b.Owner, inRes) < inAmt) return;      // idle until fed

            var s = StockOf(b.Owner);
            s[(int)inRes] -= inAmt;
            s[(int)outRes] += outAmt;
            b.BuildTimer = 0;
        }

        // Nearest node of the given resource within the building's reach, ties
        // broken by node id.
        ResourceNode NearestResource(Building wb, ResourceType res)
        {
            ResourceNode best = null;
            long bestD = long.MaxValue;
            long reach = (long)WorkRange * WorkRange;
            foreach (var n in Nodes)                  // id order
            {
                if (n.Type != res || n.Amount <= 0) continue;
                // A node the worker could never reach — one buried under a
                // building — is no node at all. Skip it, or the building hands its
                // worker an assignment it can only stand and stare at.
                if (!Map.Passable(n.X, n.Y)) continue;
                long dx = n.X - wb.CenterX, dy = n.Y - wb.CenterY;
                long d = dx * dx + dy * dy;
                if (d <= reach && d < bestD) { bestD = d; best = n; }
            }
            return best;
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

                if (u.TargetBuildingId != 0) { SiegeBuilding(u); continue; }
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

                var d = DesignOf(u.DesignId);
                int dist = Fixed.VLen(target.X - u.X, target.Y - u.Y);

                if (dist <= d.RangeFixed)
                {
                    // In reach: hold position and strike on cooldown.
                    u.Path = null;
                    u.PathIndex = 0;
                    u.Tx = u.X;
                    u.Ty = u.Y;

                    if (u.AttackTimer == 0)
                    {
                        target.Hp -= _rng.NextInt(d.Damage - 2, d.Damage + 3);
                        u.AttackTimer = d.Cooldown;
                        ShotsThisTick.Add(new Shot { FromX = u.X, FromY = u.Y, ToX = target.X, ToY = target.Y });
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

        // Besiege a building: close to its wall, then batter it on cooldown.
        // Damage comes from the same RNG as unit combat, drawn in the same
        // id-ordered sequence, so it stays deterministic. A destroyed target
        // clears itself here; the rubble is cleared in RemoveDestroyedBuildings.
        void SiegeBuilding(Unit u)
        {
            var b = Buildings.Find(x => x.Id == u.TargetBuildingId);
            if (b == null || !b.Alive) { u.TargetBuildingId = 0; return; }

            var d = DesignOf(u.DesignId);
            if (DistToBuilding(u, b) <= d.RangeFixed)
            {
                u.Path = null; u.PathIndex = 0; u.Tx = u.X; u.Ty = u.Y;
                if (u.AttackTimer == 0)
                {
                    b.Hp -= _rng.NextInt(d.Damage - 2, d.Damage + 3);
                    u.AttackTimer = d.Cooldown;
                    // The blow lands on the part of the structure the unit is
                    // actually standing against, NOT the centre. Reach is measured
                    // to the nearest footprint tile (see DistToBuilding), so
                    // recording the centre made every blow look longer than it
                    // was: against a 3x3 keep a soldier in melee logged a
                    // 2.4-tile strike, which the renderer classified as ranged and
                    // drew an arrow for. Presentation only — ShotsThisTick is
                    // transient and never hashed — but it was wrong on screen and
                    // it made melee siege sound like archery.
                    ShotsThisTick.Add(new Shot
                    {
                        FromX = u.X, FromY = u.Y,
                        ToX = Clamp(u.X, Fixed.FromInt(b.X), Fixed.FromInt(b.X + b.W - 1)),
                        ToY = Clamp(u.Y, Fixed.FromInt(b.Y), Fixed.FromInt(b.Y + b.H - 1)),
                    });
                }
            }
            else if (!u.HasPath || TickNumber % ChaseRepathEvery == 0)
            {
                // Walk to a tile touching the footprint. If none is reachable
                // (fully walled in), the unit simply can't get to it.
                var spot = SpawnPointAround(b);
                if (spot.HasValue) Order(u, spot.Value.X, spot.Value.Y);
            }
        }

        // Distance from a unit to the nearest tile of a building's footprint, in
        // fixed-point — so a unit standing against any face of a big keep is "in
        // range", not just one near its centre.
        int DistToBuilding(Unit u, Building b)
        {
            int cx = Clamp(u.X, Fixed.FromInt(b.X), Fixed.FromInt(b.X + b.W - 1));
            int cy = Clamp(u.Y, Fixed.FromInt(b.Y), Fixed.FromInt(b.Y + b.H - 1));
            return Fixed.VLen(cx - u.X, cy - u.Y);
        }

        // Clear destroyed buildings: their footprint becomes walkable rubble, a
        // razed keep stops being a drop-off, and the building leaves the list
        // (surviving order preserved). Besiegers whose target is now gone clear
        // themselves next tick.
        void RemoveDestroyedBuildings()
        {
            for (int i = Buildings.Count - 1; i >= 0; i--)
            {
                var b = Buildings[i];
                if (b.Alive) continue;
                BlockFootprint(b, false);
                if (b.Type == BuildingType.Keep) _dropOff.Remove(b.Owner);
                // A razed work building lets its peasant go — it stops working and
                // rejoins the idle pool (still a peasant, ready to be re-hired),
                // rather than serving a building that is gone.
                if (NeedsWorker(b.Type) && b.WorkerId != 0)
                {
                    var w = Units.Find(u => u.Id == b.WorkerId);
                    if (w != null) EndJob(w);
                }
                Buildings.RemoveAt(i);
            }
        }

        // Nearest living enemy within aggro range, ties broken by id so every
        // machine acquires the same one. No RNG here — acquisition is pure
        // geometry, only the damage roll is random.
        //
        // With fog on, an enemy your side cannot see is not a candidate — the
        // gate on the Attack command would be pointless if units then auto-locked
        // onto whatever was hiding behind the ridge. Note this reads the OWNER's
        // sight, not the individual unit's: an army shares what its scouts see,
        // which is both how the genre works and cheaper than per-unit vision.
        //
        // A target already engaged is NOT dropped when it slips into fog. Units
        // chase what they are fighting; a soldier that forgot its opponent the
        // instant it stepped behind a rock would look broken, and "you may not
        // START a fight you cannot see" is the rule that actually matters.
        Unit AcquireNearestEnemy(Unit u)
        {
            Unit best = null;
            int bestDist = int.MaxValue;
            foreach (var v in Units)
            {
                if (v.Owner == u.Owner || !v.Alive) continue;
                if (!CanSeeUnit(u.Owner, v)) continue;
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
            Mix(_nextBuildingId);
            Mix(unchecked((int)_rng.State));   // the dice must be in the same place

            // The design roster: two machines with different designs would build
            // units with different stats from the same id and diverge.
            Mix(_designs.Count);
            foreach (var d in _designs)
            {
                Mix(d.Hp); Mix(d.Damage); Mix(d.SpeedStat); Mix(d.RangeStat); Mix(d.Cooldown);
            }

            foreach (var u in Units)
            {
                Mix(u.Id); Mix(u.Owner); Mix(u.DesignId); Mix(u.X); Mix(u.Y);
                Mix(u.Hp); Mix(u.MaxHp);
                Mix(u.Tx); Mix(u.Ty);
                Mix(u.TargetId); Mix(u.TargetBuildingId); Mix(u.AttackTimer);
                Mix((int)u.Job); Mix(u.GatherNodeId);
                Mix((int)u.CarryType); Mix(u.CarryAmount); Mix(u.GatherTimer);
                Mix(u.IsPeasant ? 1 : 0);

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

            foreach (var b in Buildings)             // id order
            {
                Mix(b.Id); Mix(b.Owner); Mix((int)b.Type);
                Mix(b.X); Mix(b.Y); Mix(b.W); Mix(b.H);
                Mix(b.Hp); Mix(b.MaxHp);
                Mix(b.TrainQueue.Count);
                foreach (int did in b.TrainQueue) Mix(did);
                Mix(b.BuildTimer);
                Mix(b.Open ? 1 : 0);
                Mix(b.WorkerId);
            }

            // Fog. Only the EXPLORED half — see Vision.cs for why the currently
            // visible half is deliberately left out. Two machines that disagree
            // about whether fog is even on would disagree about which orders are
            // legal, so the flag itself is hashed too.
            Mix(FogEnabled ? 1 : 0);
            if (FogEnabled) Fog.MixInto(Mix);
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
