// PathFollowing — units follow smoothed routes around obstacles, in sync.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("PathFollowing — units follow smoothed routes, in sync\n");

        OpenGroundSmoothsToOneLeg();
        RoutesAroundAWall();
        ArrivesWhereItWasSent();
        AvoidsMarshWhenItCan();
        UnreachableAndBlockedOrders();
        StateChecksumAgreesAcrossClients();
        StateChecksumCatchesDivergentMaps();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // The property that protects 0xB1A7A676: with nothing in the way, a route is
    // one straight leg, so the unit moves exactly as it did before pathfinding.
    static void OpenGroundSmoothsToOneLeg()
    {
        Console.WriteLine("open ground:");
        var sim = new Simulation(TileMap.Open(64));
        var u = sim.SpawnUnit(1, 4, 4);

        Order(sim, u, 40, 30);
        Check($"a clear route smooths to a single waypoint ({u.Path?.Count ?? 0})",
              u.Path != null && u.Path.Count == 1);
        Check("and that waypoint is the destination",
              u.Path[0].Equals(new Tile(40, 30)));

        // Straight-line motion: the goal is down-and-right, so neither axis should
        // ever step the wrong way — no wandering along tile centres.
        int backtracks = 0;
        int prevX = u.X, prevY = u.Y;
        for (int i = 0; i < 500 && u.HasPath; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (u.X < prevX || u.Y < prevY) backtracks++;
            prevX = u.X; prevY = u.Y;
        }
        Check("the unit never back-tracks on a straight route", backtracks == 0);
    }

    static void RoutesAroundAWall()
    {
        Console.WriteLine("\nrouting around a wall:");
        // A vertical wall with a gap near the bottom, between unit and goal.
        var rows = new List<string>();
        for (int y = 0; y < 12; y++)
        {
            var chars = new char[16];
            for (int x = 0; x < 16; x++)
                chars[x] = (x == 8 && y < 9) ? '#' : '.';
            rows.Add(new string(chars));
        }
        var sim = new Simulation(TileMap.FromRows(rows.ToArray()));
        var u = sim.SpawnUnit(1, 2, 2);

        Order(sim, u, 14, 2);
        Check("a blocked straight line produces a multi-leg route",
              u.Path != null && u.Path.Count >= 2);

        bool touchedWall = false;
        for (int i = 0; i < 4000 && u.HasPath; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (sim.Map.At(Fixed.ToInt(u.X), Fixed.ToInt(u.Y)) == Terrain.Rock) touchedWall = true;
        }
        Check("the unit never stands on a wall tile", !touchedWall);
        Check($"and reaches the far side (at {Fixed.ToInt(u.X)},{Fixed.ToInt(u.Y)})",
              Fixed.ToInt(u.X) == 14 && Fixed.ToInt(u.Y) == 2);
    }

    static void ArrivesWhereItWasSent()
    {
        Console.WriteLine("\narrival is exact:");
        var sim = new Simulation(TileMap.Open(64));
        var u = sim.SpawnUnit(1, 8, 8);
        Order(sim, u, 33, 25);

        for (int i = 0; i < 1000 && u.HasPath; i++) sim.Tick(Array.Empty<Command>());

        Check("lands exactly on the target tile",
              u.X == Fixed.FromInt(33) && u.Y == Fixed.FromInt(25));
        Check("and the path is cleared once arrived", !u.HasPath && u.Path == null);

        // A parked, path-less unit does not drift.
        for (int i = 0; i < 50; i++) sim.Tick(Array.Empty<Command>());
        Check("a parked unit does not drift",
              u.X == Fixed.FromInt(33) && u.Y == Fixed.FromInt(25));
    }

    static void AvoidsMarshWhenItCan()
    {
        Console.WriteLine("\nterrain cost is respected:");
        // A marsh belt with clear ground one row above; the straight line crosses it.
        var sim = new Simulation(TileMap.FromRows(
            "................",
            "................",
            "....,,,,,,,,....",
            "................"));
        var u = sim.SpawnUnit(1, 1, 2);          // start ON the marsh row
        Order(sim, u, 14, 2);

        int marshTilesStoodOn = 0;
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < 4000 && u.HasPath; i++)
        {
            sim.Tick(Array.Empty<Command>());
            var cell = (Fixed.ToInt(u.X), Fixed.ToInt(u.Y));
            if (seen.Add(cell) && sim.Map.At(cell.Item1, cell.Item2) == Terrain.Marsh)
                marshTilesStoodOn++;
        }
        Check($"routes mostly around the marsh (touched {marshTilesStoodOn} marsh tiles)",
              marshTilesStoodOn <= 2);
        Check("still arrives", Fixed.ToInt(u.X) == 14 && Fixed.ToInt(u.Y) == 2);
    }

    static void UnreachableAndBlockedOrders()
    {
        Console.WriteLine("\nbad orders leave the unit as it was:");
        var sim = new Simulation(TileMap.FromRows(
            ".....",
            ".###.",
            ".#.#.",     // (2,2) sealed inside rock
            ".###.",
            "....."));
        var u = sim.SpawnUnit(1, 0, 0);

        Order(sim, u, 2, 2);            // sealed pocket
        Check("an unreachable order is ignored (no path set)", !u.HasPath);

        Order(sim, u, 1, 1);           // onto rock
        Check("an order onto solid rock is ignored", !u.HasPath);

        // A click off the edge is clamped, not dropped — it must still move.
        var open = new Simulation(TileMap.Open(32));
        var v = open.SpawnUnit(1, 5, 5);
        Order(open, v, 999, 5);
        Check("an off-map click is clamped to the edge and still moves",
              v.HasPath && v.Path[^1].X == open.Map.Width - 1);
    }

    // The real determinism test: two clients, identical orders, StateChecksum
    // (which now includes every unit's remaining path) equal on every tick.
    static void StateChecksumAgreesAcrossClients()
    {
        Console.WriteLine("\ntwo clients stay in sync with paths in play:");
        var (a, b) = TwoClients();

        // Same orders to both, at staggered ticks, over the obstacle map.
        var script = new Dictionary<int, Action>
        {
            [1]  = () => { a.Issue(Move(1, 40, 30)); b.Issue(Move(1, 40, 30)); },
            [2]  = () => { a.Issue(Move(2, 20, 30)); b.Issue(Move(2, 20, 30)); },
            [30] = () => { a.Issue(Move(4, 5, 5));   b.Issue(Move(4, 5, 5)); },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 600; t++)
        {
            if (script.TryGetValue(t, out var act)) act();
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum())
            {
                if (first < 0) first = t;
                desyncs++;
            }
        }
        Check($"StateChecksum identical on all 600 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both clients reached tick 600 in agreement",
              a.Sim.StateChecksum() == b.Sim.StateChecksum() && a.Sim.TickNumber == 600);

        // Frozen hash still tracks too — paths must not have leaked into it.
        Check("the frozen Checksum() also still agrees", a.Sim.Checksum() == b.Sim.Checksum());
    }

    // Two machines that loaded different maps must be caught immediately, not
    // after their units diverge fifty ticks into the first detour.
    static void StateChecksumCatchesDivergentMaps()
    {
        Console.WriteLine("\ndifferent maps are caught at once:");
        var open = new Simulation(TileMap.Open(64));
        var demo = new Simulation(TileMap.Demo(64));
        open.SpawnUnit(1, 8, 8);
        demo.SpawnUnit(1, 8, 8);

        Check("identical unit state but different terrain -> different StateChecksum",
              open.StateChecksum() != demo.StateChecksum());
        Check("yet the frozen Checksum() (units only) is identical",
              open.Checksum() == demo.Checksum());

        var open2 = new Simulation(TileMap.Open(64));
        open2.SpawnUnit(1, 8, 8);
        Check("the same map gives the same StateChecksum",
              open.StateChecksum() == open2.StateChecksum());
    }

    // ---- helpers -----------------------------------------------------------

    // Issue a single move to one sim through the real command path, and advance
    // the tick that applies it — exactly what Apply -> Order does in a match.
    static void Order(Simulation sim, Unit u, int gx, int gy) =>
        sim.Tick(new List<Command>
        {
            new Command { Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = gx, Y = gy },
        });

    static (Client, Client) TwoClients()
    {
        // Two independent, identical maps — the way two machines build the same
        // map from the same authoring. TileMap.Demo is deterministic, so both
        // fingerprints match.
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Demo());
        var b = new Client(2, net, TileMap.Demo());
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.SpawnUnit(1, 8, 8);
            c.Sim.SpawnUnit(1, 11, 8);
            c.Sim.SpawnUnit(1, 8, 11);
            c.Sim.SpawnUnit(2, 44, 40);
            c.Sim.SpawnUnit(2, 47, 40);
        }
        return (a, b);
    }

    static Command Move(int unit, int x, int y) => new Command
    {
        Type = CommandType.Move, UnitIds = new[] { unit }, X = x, Y = y,
    };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
