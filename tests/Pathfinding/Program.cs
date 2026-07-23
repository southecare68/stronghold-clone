// Pathfinding — map, RNG and grid A*: correct, and above all deterministic.
//
// Correctness here is the easy half. The half that matters for a lockstep RTS is
// that the SAME query always returns the SAME route — not merely a route of the
// same length. Two machines that pick different shortest paths send their units
// different ways, and the checksums part company a few ticks later.

using System;
using System.Collections.Generic;
using System.Text;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Pathfinding — map, RNG, and deterministic grid A*\n");

        RngIsDeterministicAndUnbiased();
        MapReadsAndGenerates();
        FindsTheObviousPath();
        WalksAroundAWall();
        RefusesTheImpossible();
        PrefersTheCheapWayRound();
        DoesNotCutBlockedCorners();
        SameQuerySameRoute();
        TheRouteIsPinned();
        BoundedWork();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void RngIsDeterministicAndUnbiased()
    {
        Console.WriteLine("deterministic RNG:");

        var a = new Rng(12345);
        var b = new Rng(12345);
        bool same = true;
        for (int i = 0; i < 1000; i++) same &= a.NextUInt() == b.NextUInt();
        Check("same seed produces the same sequence", same);

        var c = new Rng(12346);
        Check("a different seed produces a different sequence",
              new Rng(12345).NextUInt() != c.NextUInt());

        // Restoring state matters: an Rng travels in a snapshot, and a rejoiner
        // whose generator is one draw behind desyncs at the next random event.
        var d = new Rng(999);
        for (int i = 0; i < 50; i++) d.NextUInt();
        var restored = new Rng(1);
        restored.Restore(d.State);
        Check("restoring state resumes the identical sequence",
              d.NextUInt() == restored.NextUInt());

        // Seed 0 would lock xorshift at zero forever.
        var zero = new Rng(0);
        Check("seed 0 does not produce a dead generator", zero.NextUInt() != 0);

        // Rejection sampling should not skew a small range. A modulo-biased
        // generator shows up clearly over enough draws.
        var rng = new Rng(7);
        var counts = new int[6];
        const int rolls = 60000;
        for (int i = 0; i < rolls; i++) counts[rng.NextInt(6)]++;
        int lo = int.MaxValue, hi = 0;
        foreach (int n in counts) { lo = Math.Min(lo, n); hi = Math.Max(hi, n); }
        Check($"a d6 is even across {rolls} rolls (min {lo}, max {hi}, expected {rolls / 6})",
              lo > rolls / 6 * 0.95 && hi < rolls / 6 * 1.05);

        var inclusive = new Rng(3);
        bool inRange = true;
        for (int i = 0; i < 1000; i++) { int v = inclusive.NextInt(3, 5); inRange &= v >= 3 && v <= 5; }
        Check("NextInt(min,max) stays inside its inclusive range", inRange);
    }

    static void MapReadsAndGenerates()
    {
        Console.WriteLine("\nmap:");
        var map = TileMap.FromRows(
            "....",
            ".~#.",
            ".,..");

        Check("dimensions read correctly", map.Width == 4 && map.Height == 3);
        Check("water is impassable", !map.Passable(1, 1));
        Check("rock is impassable", !map.Passable(2, 1));
        Check("marsh is passable but slow",
              map.Passable(1, 2) && map.EnterCost(1, 2) == TileMap.MarshCost);
        Check("ground is cheap", map.EnterCost(0, 0) == TileMap.StepCost);
        Check("outside the map is impassable, not a crash",
              !map.Passable(-1, 0) && !map.Passable(0, 99));

        // Generation must be reproducible or a match desyncs before it starts.
        var g1 = TileMap.Generate(60, 40, 2024);
        var g2 = TileMap.Generate(60, 40, 2024);
        var g3 = TileMap.Generate(60, 40, 2025);
        Check("the same seed generates an identical map", MapsEqual(g1, g2));
        Check("a different seed generates a different map", !MapsEqual(g1, g3));
    }

    static void FindsTheObviousPath()
    {
        Console.WriteLine("\nbasic routing:");
        var map = new TileMap(10, 10);
        var pf = new PathFinder(map);
        var path = new List<Tile>();

        Check("start == goal succeeds with an empty path",
              pf.TryFindPath(3, 3, 3, 3, path) && path.Count == 0);

        Check("finds a straight-line path", pf.TryFindPath(0, 0, 5, 0, path));
        Check($"which is 5 steps and ends on the goal ({path.Count})",
              path.Count == 5 && path[^1].Equals(new Tile(5, 0)));

        Check("finds a diagonal path", pf.TryFindPath(0, 0, 5, 5, path));
        Check($"which takes 5 diagonal steps, not 10 orthogonal ones ({path.Count})",
              path.Count == 5 && path[^1].Equals(new Tile(5, 5)));

        Check("the path never includes the starting tile",
              !path.Contains(new Tile(0, 0)));
    }

    static void WalksAroundAWall()
    {
        Console.WriteLine("\nobstacles:");
        //  a wall with one gap at the bottom
        var map = TileMap.FromRows(
            ".....#....",
            ".....#....",
            ".....#....",
            ".....#....",
            "..........");
        var pf = new PathFinder(map);
        var path = new List<Tile>();

        Check("routes around a wall", pf.TryFindPath(0, 0, 9, 0, path));
        Check("and goes through the gap rather than through the wall",
              !path.Contains(new Tile(5, 0)) && path.Contains(new Tile(5, 4)));
        Check("ending on the goal", path[^1].Equals(new Tile(9, 0)));
    }

    static void RefusesTheImpossible()
    {
        Console.WriteLine("\nimpossible requests:");
        var map = TileMap.FromRows(
            ".....",
            ".###.",
            ".#..#",     // the pocket at (2,2),(3,2) is sealed
            ".####",
            ".....");
        var pf = new PathFinder(map);
        var path = new List<Tile>();

        Check("no path into a sealed pocket", !pf.TryFindPath(0, 0, 2, 2, path));
        Check("and the output is left empty", path.Count == 0);
        Check("a goal inside rock is refused", !pf.TryFindPath(0, 0, 1, 1, path));
        Check("a start inside rock is refused", !pf.TryFindPath(1, 1, 0, 0, path));
        Check("an off-map goal is refused, not a crash",
              !pf.TryFindPath(0, 0, 99, 99, path));
    }

    static void PrefersTheCheapWayRound()
    {
        Console.WriteLine("\nterrain cost:");
        // A short slog through marsh, or a longer walk on clear ground. The
        // marsh detour is only 3 tiles; going round costs more steps but less.
        var map = TileMap.FromRows(
            "..,,,..",
            "..,,,..",
            ".......");
        var pf = new PathFinder(map);
        var path = new List<Tile>();

        Check("finds a route across a marsh belt", pf.TryFindPath(0, 0, 6, 0, path));
        int marshTiles = 0;
        foreach (var t in path) if (map.At(t.X, t.Y) == Terrain.Marsh) marshTiles++;
        Check($"and goes around the marsh rather than through it ({marshTiles} marsh tiles)",
              marshTiles == 0);

        // With no way round, it should still cross rather than give up.
        var forced = TileMap.FromRows("..,,,..");
        var pf2 = new PathFinder(forced);
        Check("but still crosses marsh when there is no alternative",
              pf2.TryFindPath(0, 0, 6, 0, path) && path.Count == 6);
    }

    static void DoesNotCutBlockedCorners()
    {
        Console.WriteLine("\ncorner cutting:");
        // (1,0) and (0,1) are both blocked, so the diagonal from (0,0) to (1,1)
        // would squeeze through a gap a player can see is shut.
        var map = TileMap.FromRows(
            ".#.",
            "#..",
            "...");
        var pf = new PathFinder(map);
        var path = new List<Tile>();

        bool found = pf.TryFindPath(0, 0, 1, 1, path);
        Check("a unit boxed in by two blocked tiles cannot slip out diagonally", !found);

        // The rule is the STRICT one: a diagonal needs both adjacent orthogonals
        // clear. With one side blocked the unit walks around the corner rather
        // than shaving it, which is what stops units clipping through walls.
        var open = TileMap.FromRows(
            ".#.",
            "...",
            "...");
        var pf2 = new PathFinder(open);
        bool routed = pf2.TryFindPath(0, 0, 1, 1, path);
        Check("with one side blocked the diagonal is refused and it goes around",
              routed && path.Count == 2 && path[0].Equals(new Tile(0, 1)));

        // Nothing blocked: the diagonal is taken, one step.
        var clear = new TileMap(3, 3);
        var pf3 = new PathFinder(clear);
        Check("with both sides clear the diagonal is a single step",
              pf3.TryFindPath(0, 0, 1, 1, path) && path.Count == 1);
    }

    // The property that keeps a match in sync: identical query, identical route.
    static void SameQuerySameRoute()
    {
        Console.WriteLine("\ndeterminism:");
        var map = TileMap.Generate(80, 60, 4242);
        var reference = new List<Tile>();
        var pf = new PathFinder(map);

        // Find a query that actually has a route on this generated map.
        bool ok = false;
        int gx = 0, gy = 0, sx = 0, sy = 0;
        for (int i = 0; i < 200 && !ok; i++)
        {
            sx = i % map.Width; sy = i % map.Height;
            gx = map.Width - 1 - (i % map.Width); gy = map.Height - 1 - (i % map.Height);
            ok = map.Passable(sx, sy) && map.Passable(gx, gy) &&
                 pf.TryFindPath(sx, sy, gx, gy, reference) && reference.Count > 20;
        }
        Check($"found a long route to test with ({reference.Count} tiles)", ok);
        if (!ok) return;

        // Repeated on the same instance — the reused buffers must not leak state
        // between searches.
        bool stable = true;
        var again = new List<Tile>();
        for (int i = 0; i < 50; i++)
        {
            pf.TryFindPath(sx, sy, gx, gy, again);
            stable &= SamePath(reference, again);
        }
        Check("the same query on the same instance repeats exactly", stable);

        // A fresh instance on a freshly generated map must agree too — this is
        // the cross-machine case, where nothing is warm and nothing is shared.
        var freshMap = TileMap.Generate(80, 60, 4242);
        var freshPf = new PathFinder(freshMap);
        var freshPath = new List<Tile>();
        freshPf.TryFindPath(sx, sy, gx, gy, freshPath);
        Check("a fresh PathFinder on a fresh map agrees tile for tile",
              SamePath(reference, freshPath));

        // Interleaving other searches must not perturb the answer either.
        pf.TryFindPath(0, 0, 40, 30, again);
        pf.TryFindPath(10, 10, 3, 50, again);
        pf.TryFindPath(sx, sy, gx, gy, again);
        Check("and is unaffected by other searches in between",
              SamePath(reference, again));
    }

    // A golden route. Length alone would not catch a tie-break change — dozens
    // of routes share the shortest length across open ground — but the exact
    // sequence of tiles would, which is the thing that has to match between two
    // machines.
    static void TheRouteIsPinned()
    {
        Console.WriteLine("\npinned route (a tie-break change breaks this):");
        var map = new TileMap(9, 9);            // wide open: maximum ambiguity
        var pf = new PathFinder(map);
        var path = new List<Tile>();
        pf.TryFindPath(0, 0, 8, 4, path);

        const string expected = "(1,1)(2,2)(3,3)(4,4)(5,4)(6,4)(7,4)(8,4)";
        string actual = Describe(path);
        Check($"open-ground route is exactly {expected}", actual == expected);
        if (actual != expected) Console.WriteLine($"        got {actual}");

        // Same start and goal, mirrored: the route must mirror too, not wander.
        pf.TryFindPath(8, 8, 0, 4, path);
        const string mirrored = "(7,7)(6,6)(5,5)(4,4)(3,4)(2,4)(1,4)(0,4)";
        Check($"the mirrored route is exactly {mirrored}", Describe(path) == mirrored);
        if (Describe(path) != mirrored) Console.WriteLine($"        got {Describe(path)}");
    }

    static void BoundedWork()
    {
        Console.WriteLine("\nbounded work:");
        // A big sealed room: the search must exhaust the region and stop, not
        // spend an unbounded slice of a tick on it.
        var map = new TileMap(200, 200);
        for (int x = 0; x < 200; x++) { map.Set(x, 100, Terrain.Rock); }
        var pf = new PathFinder(map) { MaxExpansions = 500 };
        var path = new List<Tile>();

        bool found = pf.TryFindPath(0, 0, 199, 199, path);
        Check("gives up rather than exceeding its expansion budget", !found);
        Check($"having expanded no more than the budget ({pf.LastExpansions})",
              pf.LastExpansions <= 501);
        Check("and leaves no partial path behind", path.Count == 0);

        // With a sane budget the same map is fine — the wall has no gap, so this
        // is genuinely unreachable, but it must terminate rather than hang.
        var pf2 = new PathFinder(map);
        Check("a walled-off goal terminates and reports failure",
              !pf2.TryFindPath(0, 0, 199, 199, path));
    }

    // ---- helpers -----------------------------------------------------------

    static bool MapsEqual(TileMap a, TileMap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.At(x, y) != b.At(x, y)) return false;
        return true;
    }

    static bool SamePath(List<Tile> a, List<Tile> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    static string Describe(List<Tile> path)
    {
        var sb = new StringBuilder();
        foreach (var t in path) sb.Append(t);
        return sb.ToString();
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
