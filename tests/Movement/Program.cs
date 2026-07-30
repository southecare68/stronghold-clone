// Movement — waypoint chains and the cautious (enemy-avoiding) march.
//
// A plain Move is one destination, pathed straight and string-pulled — the direct
// march this game shares with Stronghold. Two options sit on top of it, both packed
// into the Move command's flags: SHIFT appends a waypoint (a queued stop the unit
// walks to after finishing the current route), and ALT marks the journey CAUTIOUS,
// weighting tiles near known enemies so A* curves wide of them.
//
// What these tests pin down: a unit walks a queued chain to its last stop; a fresh
// order clears the queue; a cautious march both detours (longer) and keeps its
// distance from an enemy sitting on the straight line; the queue and the cautious
// flag survive a snapshot; and — the one that matters for a match — two machines
// agree on every tick of a march with waypoints and caution in play.
//
// Sim-only, like the other suites. Run with `dotnet run`.

using System;
using System.Collections.Generic;
using Sim;
using Netcode;

static class Program
{
    static int _failures;
    static readonly List<Command> None = new();

    // Move-command flags (see the Move case in Simulation.Apply).
    const int Append = 1, Cautious = 2;

    static void Main()
    {
        Console.WriteLine("Movement — waypoints & the cautious march\n");

        AUnitWalksAChainOfWaypoints();
        AFreshOrderClearsTheQueue();
        ACautiousMarchGivesAnEnemyAWideBerth();
        APlainMarchIgnoresEnemies();
        ASnapshotCarriesTheQueueAndCaution();
        TwoClientsAgreeOnAMarchWithWaypointsAndCaution();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // Shift-appended stops form a chain: the unit walks each in turn and ends on the
    // last, the queue drained. The three-stop L could not be reached any other way —
    // a fresh order would have gone straight to the final point.
    static void AUnitWalksAChainOfWaypoints()
    {
        Console.WriteLine("a unit walks a chain of waypoints:");
        var sim = new Simulation(TileMap.Open(48));
        var u = sim.SpawnUnit(1, 4, 4);

        Order(sim, Move(1, u.Id, 30, 4, 0));           // first stop
        Order(sim, Move(1, u.Id, 30, 30, Append));     // queued
        Order(sim, Move(1, u.Id, 4, 30, Append));      // queued
        Check("two stops are queued beyond the current route", u.Waypoints.Count == 2);

        for (int i = 0; i < 6000 && (u.HasPath || u.Waypoints.Count > 0); i++) sim.Tick(None);
        Check("it arrives at the final stop", AtTile(u, 4, 30));
        Check("and the queue is drained", u.Waypoints.Count == 0 && !u.HasPath);
    }

    // A plain (non-append) order replaces the whole plan — queued stops are dropped.
    static void AFreshOrderClearsTheQueue()
    {
        Console.WriteLine("\na fresh order clears the queue:");
        var sim = new Simulation(TileMap.Open(48));
        var u = sim.SpawnUnit(1, 4, 4);

        Order(sim, Move(1, u.Id, 30, 4, 0));
        Order(sim, Move(1, u.Id, 30, 30, Append));
        Order(sim, Move(1, u.Id, 4, 30, Append));
        Check("stops are queued", u.Waypoints.Count == 2);

        Order(sim, Move(1, u.Id, 10, 10, 0));          // a fresh, non-append order
        Check("the fresh order emptied the queue", u.Waypoints.Count == 0);
    }

    // A cautious march past an enemy sitting on the straight line both detours (a
    // longer route) and keeps clear of the enemy, where the direct route runs right
    // over it. Two fresh sims so each route is measured from the same start.
    static void ACautiousMarchGivesAnEnemyAWideBerth()
    {
        Console.WriteLine("\na cautious march gives an enemy a wide berth:");

        var (dirLen, dirNear) = MarchPastFoe(cautious: false);
        var (cauLen, cauNear) = MarchPastFoe(cautious: true);

        Check($"the direct route runs almost over the enemy ({dirNear:0.0} tiles)", dirNear < 1.0);
        Check($"the cautious route detours — it is longer ({cauLen:0.0} > {dirLen:0.0})", cauLen > dirLen + 1);
        Check($"and it keeps its distance ({cauNear:0.0} > {dirNear:0.0})", cauNear > dirNear + 2);
    }

    // Sets a lone unit marching straight past a single enemy on the line; returns the
    // route's length and its nearest approach to the enemy.
    static (double Len, double Near) MarchPastFoe(bool cautious)
    {
        var sim = new Simulation(TileMap.Open(40));    // fog off by default → the owner sees the foe
        var u = sim.SpawnUnit(1, 2, 20);
        sim.SpawnUnit(2, 20, 20);                      // an enemy soldier on the straight line
        Order(sim, Move(1, u.Id, 38, 20, cautious ? Cautious : 0));
        return (PathLen(2, 20, u.Path), NearestApproach(2, 20, u.Path, 20, 20));
    }

    // The regression guard: a plain march is blind to enemies — it still cuts the
    // straight line right past the foe, exactly as before caution existed.
    static void APlainMarchIgnoresEnemies()
    {
        Console.WriteLine("\na plain march still cuts straight past enemies:");
        var (_, near) = MarchPastFoe(cautious: false);
        Check($"the plain route passes right over the enemy ({near:0.0} tiles)", near < 1.0);
    }

    // Queued stops and the cautious flag are game state, so they must ride the
    // snapshot a rejoiner adopts — verified by the snapshot reproducing the host's
    // full-state checksum.
    static void ASnapshotCarriesTheQueueAndCaution()
    {
        Console.WriteLine("\na snapshot carries the queue and the cautious flag:");
        var host = new Simulation(TileMap.Open(48));
        var u = host.SpawnUnit(1, 4, 4);
        Order(host, Move(1, u.Id, 30, 4, Cautious));       // a cautious journey…
        Order(host, Move(1, u.Id, 30, 30, Append));        // …with two queued stops
        Order(host, Move(1, u.Id, 4, 30, Append));
        Check("the host has a queued, cautious march", u.Waypoints.Count == 2 && u.Cautious);

        uint hostSum = host.Snapshot().Checksum;
        var back = Wire.DeserializeSnapshot(Wire.Serialize(host.Snapshot()));
        var fresh = new Simulation(TileMap.Open(48));
        fresh.Restore(back);
        Check($"a rejoiner reproduces the host (0x{fresh.StateChecksum():X8} == 0x{hostSum:X8})",
              fresh.StateChecksum() == hostSum);

        var ru = fresh.Units.Find(v => v.Id == u.Id);
        Check("and the rejoiner has the same two stops", ru != null && ru.Waypoints.Count == 2);
        Check("and the same cautious flag", ru != null && ru.Cautious);
    }

    // The one that matters most: two clients running the same march — a waypoint
    // chain AND a cautious leg, with an enemy in the field to cast danger — agree on
    // every tick.
    static void TwoClientsAgreeOnAMarchWithWaypointsAndCaution()
    {
        Console.WriteLine("\ntwo clients agree on a march with waypoints and caution:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(64));
        var b = new Client(2, net, TileMap.Open(64));
        net.Connect(a);
        net.Connect(b);

        int marcher = 0, foe = 0;
        foreach (var c in new[] { a, b })
        {
            var m = c.Sim.SpawnUnit(1, 4, 32);
            var f = c.Sim.SpawnUnit(2, 32, 32);        // an enemy to route around
            marcher = m.Id; foe = f.Id;
        }

        int desyncs = 0, first = -1;
        for (int t = 0; t < 1600; t++)
        {
            if (t == 10) { a.Issue(Move(1, marcher, 60, 32, Cautious)); b.Issue(Move(1, marcher, 60, 32, Cautious)); }
            if (t == 12) { a.Issue(Move(1, marcher, 60, 60, Append));   b.Issue(Move(1, marcher, 60, 60, Append)); }
            if (t == 14) { a.Issue(Move(1, marcher, 4, 60, Append));    b.Issue(Move(1, marcher, 4, 60, Append)); }

            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 1600 ticks" + (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        var au = a.Sim.Units.Find(v => v.Id == marcher);
        Check("and the marcher moved off along the chain",
              au != null && !(Fixed.ToInt(au.X) == 4 && Fixed.ToInt(au.Y) == 32));
    }

    // ---- helpers -----------------------------------------------------------

    static Command Move(int owner, int id, int x, int y, int flags) =>
        new Command { Owner = owner, Type = CommandType.Move, UnitIds = new[] { id }, X = x, Y = y, TargetId = flags };

    static void Order(Simulation sim, Command c) => sim.Tick(new List<Command> { c });

    static bool AtTile(Unit u, int x, int y) => Fixed.ToInt(u.X) == x && Fixed.ToInt(u.Y) == y;

    static double PathLen(int sx, int sy, List<Tile> path)
    {
        if (path == null) return 0;
        double len = 0, ax = sx, ay = sy;
        foreach (var t in path) { len += Math.Sqrt((t.X - ax) * (t.X - ax) + (t.Y - ay) * (t.Y - ay)); ax = t.X; ay = t.Y; }
        return len;
    }

    static double NearestApproach(int sx, int sy, List<Tile> path, int fx, int fy)
    {
        if (path == null) return double.MaxValue;
        double min = double.MaxValue, ax = sx, ay = sy;
        foreach (var t in path) { min = Math.Min(min, PointToSegment(fx, fy, ax, ay, t.X, t.Y)); ax = t.X; ay = t.Y; }
        return min;
    }

    static double PointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double s = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double cx = ax + s * dx, cy = ay + s * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
