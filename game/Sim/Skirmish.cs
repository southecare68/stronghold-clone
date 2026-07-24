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
        public static IEnumerable<(ResourceType Type, int X, int Y, int Amount)> Nodes(int size)
        {
            int w = West(size), e = East(size), m = MidY(size);
            yield return (ResourceType.Wood, w + 6, m - 10, 400);
            yield return (ResourceType.Stone, w + 6, m + 10, 400);
            yield return (ResourceType.Wood, e - 4, m - 10, 400);
            yield return (ResourceType.Stone, e - 4, m + 10, 400);
            yield return (ResourceType.Wood, size / 2 - 8, size * 25 / 100, 500);
            yield return (ResourceType.Stone, size / 2 + 6, size * 75 / 100, 500);
        }

        // Build the starting world. The ORDER of these calls is part of the
        // contract: ids are handed out in sequence, so shuffling them would give
        // two clients different ids for the same unit and desync the match.
        public static void Setup(Simulation sim, int size = DefaultSize)
        {
            int w = West(size), e = East(size), m = MidY(size);

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
            }

            foreach (var (type, x, y, amount) in Nodes(size))
                sim.SpawnNode(type, x, y, amount);

            foreach (var d in Designs())
                sim.RegisterDesign(d);
        }
    }
}
