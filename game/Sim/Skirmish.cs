// Skirmish.cs — the 1v1 starting position, in one place.
//
// This lives in Sim rather than in the Godot layer for two reasons. The first is
// determinism: every machine in a match must build a byte-identical world before
// tick 0, so there must be exactly one definition of "the start", not one per
// call site. The second is that a map layout can be WRONG in ways a compiler
// can't see — a resource node dropped into a lake, a keep straddling the ridge,
// a base walled off from the passes. Putting the layout here lets the headless
// tests place it for real and check it, which is how the south node was caught
// sitting in water.
//
// Nothing in here is random. Every coordinate is a fixed fraction of `size`, so
// the same size always produces the same start.

using System.Collections.Generic;

namespace Sim
{
    public static class Skirmish
    {
        public const int DefaultSize = 128;

        // Peasants each side starts with. Enough spare hands to man the whole
        // food chain (farm + mill + bakery = three workers) AND a woodcutter or
        // quarry at the same time — with only four you ran dry before the bakery,
        // so grain and flour piled up and no bread was ever baked, which stalled
        // population growth in turn. Six clears that cold-start deadlock.
        public const int StartPeasants = 6;

        // The two bases face each other across the ridge that TileMap.Skirmish
        // runs down the middle.
        public static int West(int size) => size * 8 / 100;
        public static int East(int size) => size * 88 / 100;
        public static int MidY(int size) => size / 2;

        // The point-buy roster, in registration order, so design ids line up on
        // every machine: 0 Soldier (the built-in default), 1 Runner, 2 Brute,
        // 3 Archer. Each spends the same budget, allocated differently — the
        // Archer trades HP and speed for reach (RangeStat 8 = 4 tiles), so it
        // softens the enemy on approach but folds fast if it gets caught.
        public static readonly string[] DesignNames = { "Soldier", "Runner", "Brute", "Archer" };

        public static IEnumerable<UnitDesign> Designs()
        {
            yield return new UnitDesign { Hp = 60, Damage = 9, SpeedStat = 10, RangeStat = 3, Cooldown = 10 };
            yield return new UnitDesign { Hp = 150, Damage = 11, SpeedStat = 3, RangeStat = 3, Cooldown = 15 };
            yield return new UnitDesign { Hp = 55, Damage = 9, SpeedStat = 6, RangeStat = 8, Cooldown = 13 };
        }

        // Where the resource nodes go. Two safe patches behind each base, and a
        // contested pair out by the northern and southern passes — the only
        // reason to leave home early.
        //
        // The home patches are deliberately inside the keep's opening sight
        // radius. With fog on, a node you cannot see is a node you cannot order a
        // worker to, and a start where both players must scout their own back
        // yard before they may gather is just a delay, not a decision.
        public static IEnumerable<(ResourceType Type, int X, int Y, int Amount)> Nodes(int size)
        {
            int w = West(size), e = East(size), m = MidY(size);

            // A contested pair of stone patches out by the passes.
            yield return (ResourceType.Stone, size / 2 - 8, size * 25 / 100, 500);
            yield return (ResourceType.Stone, size / 2 + 6, size * 75 / 100, 500);

            // A forest and a stone deposit by each base — clusters for a
            // woodcutter's hut and a quarry to work. Kept in the keep's opening
            // sight so you can build both from tick 0. A grid rather than a
            // scatter, so both machines plant the identical nodes in the identical
            // order (id assignment is part of the deterministic contract).
            foreach (var t in Cluster(ResourceType.Wood, w + 7, m - 9, 120)) yield return t;
            foreach (var t in Cluster(ResourceType.Stone, w + 6, m + 9, 160)) yield return t;
            foreach (var t in Cluster(ResourceType.Wood, e - 5, m - 9, 120)) yield return t;
            foreach (var t in Cluster(ResourceType.Stone, e - 4, m + 9, 160)) yield return t;

            // An iron seam out toward the open flank of each base — a little further
            // to reach than the wood and stone, for an iron mine to work. Scarcer
            // than stone, so iron stays the premium good.
            foreach (var t in Cluster(ResourceType.Iron, w + 14, m, 100)) yield return t;
            foreach (var t in Cluster(ResourceType.Iron, e - 12, m, 100)) yield return t;
        }

        // A 3x3 block of a resource around (cx, cy), each a node. A cluster is
        // worked down over a match, not endless — a forest of ~1000 wood, a stone
        // deposit of ~1400.
        static IEnumerable<(ResourceType, int, int, int)> Cluster(ResourceType res, int cx, int cy, int per)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    yield return (res, cx + dx * 2, cy + dy * 2, per);
        }

        // Build the starting world. The ORDER of these calls is part of the
        // contract: ids are handed out in sequence, so shuffling them would give
        // two clients different ids for the same unit and desync the match.
        public static void Setup(Simulation sim, int size = DefaultSize)
        {
            int w = West(size), e = East(size), m = MidY(size);

            // Fog of war on. It is opt-in at the sim level so the older suites
            // keep testing what they were written to test, and this is the switch
            // that turns it on for an actual match. Set BEFORE anything is placed
            // so both machines agree from tick 0.
            sim.FogEnabled = true;

            // Mirrored starting parties, either side of the ridge.
            sim.SpawnUnit(1, w + 4, m - 2);
            sim.SpawnUnit(1, w + 4, m);
            sim.SpawnUnit(1, w + 4, m + 2);
            sim.SpawnUnit(2, e - 2, m - 2);
            sim.SpawnUnit(2, e - 2, m);
            sim.SpawnUnit(2, e - 2, m + 2);

            // A keep each — it sets the drop-off — plus something to build with.
            sim.PlaceBuilding(BuildingType.Keep, 1, w, m - 1);
            sim.PlaceBuilding(BuildingType.Keep, 2, e, m - 1);
            foreach (int owner in new[] { 1, 2 })
            {
                sim.AddResource(owner, ResourceType.Wood, 200);
                sim.AddResource(owner, ResourceType.Stone, 100);
                // A larder to open on. Rations draw down food every realm tick, and
                // the food chain (farm -> mill -> bakery) takes a good while to lay
                // and start baking. Without an opening stock the people would go
                // hungry, popularity would slide, and the idle hands you need to man
                // that very chain would drift off before it produced a loaf. Sixty
                // covers the ramp-up for a starting six at full rations.
                sim.AddResource(owner, ResourceType.Food, 60);
                // A starting workforce to bootstrap the economy: peasants staff
                // your work buildings, and the bread they help make breeds more.
                // Placed AFTER the keep so they spawn at its drop-off.
                for (int i = 0; i < StartPeasants; i++) sim.SpawnPeasant(owner);
            }

            foreach (var (type, x, y, amount) in Nodes(size))
                sim.SpawnNode(type, x, y, amount);

            foreach (var d in Designs())
                sim.RegisterDesign(d);
        }
    }
}
