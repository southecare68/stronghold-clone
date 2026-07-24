// Walls — curtain walls seal routes; gatehouses open and close them.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Walls — curtain walls and gatehouses\n");

        AWallLineSealsARoute();
        AGateOpensAndClosesTheGap();
        WallsAndGatesCostAndValidate();
        TwoClientsAgreeOnWallsAndGates();
        GateStateSurvivesARejoin();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // A wall built across the only corridor cuts it; a gap in the wall is the
    // only way through.
    static void AWallLineSealsARoute()
    {
        Console.WriteLine("a wall line seals a route:");
        // A one-tile-tall corridor between two solid rock rows.
        var sim = new Simulation(TileMap.FromRows(
            "##########",
            "..........",
            "##########"));
        var pf = new PathFinder(sim.Map);
        var path = new List<Tile>();
        Check("the corridor is open to begin with", pf.TryFindPath(0, 1, 9, 1, path));

        // Wall the corridor at x=5.
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 5, 1);
        Check("the wall was placed", wall != null);
        Check("its tile is now impassable", !sim.Map.Passable(5, 1));
        Check("and the corridor is sealed", !pf.TryFindPath(0, 1, 9, 1, path));
    }

    static void AGateOpensAndClosesTheGap()
    {
        Console.WriteLine("\na gatehouse opens and closes the gap:");
        var sim = new Simulation(TileMap.FromRows(
            "##########",
            "..........",
            "##########"));
        var pf = new PathFinder(sim.Map);
        var path = new List<Tile>();

        // A gatehouse across the corridor starts CLOSED — it blocks like a wall.
        var gate = sim.PlaceBuilding(BuildingType.Gatehouse, 1, 5, 1);
        Check("a new gate starts closed and blocking",
              !gate.Open && !sim.Map.Passable(5, 1) && !pf.TryFindPath(0, 1, 9, 1, path));

        // Open it: the tile becomes walkable and the route reappears.
        Toggle(sim, 1, gate.Id);
        Check("opening the gate makes its tile passable", gate.Open && sim.Map.Passable(5, 1));
        Check("and the route is open again", pf.TryFindPath(0, 1, 9, 1, path));
        Check("the path runs through the gate tile",
              path.Contains(new Tile(5, 1)));

        // Close it again: sealed once more.
        Toggle(sim, 1, gate.Id);
        Check("closing the gate blocks it again",
              !gate.Open && !sim.Map.Passable(5, 1) && !pf.TryFindPath(0, 1, 9, 1, path));

        // Only the owner may work the gate.
        Toggle(sim, 2, gate.Id);
        Check("an enemy cannot toggle your gate", !gate.Open);
    }

    static void WallsAndGatesCostAndValidate()
    {
        Console.WriteLine("\nwalls and gates cost and validate:");
        var sim = new Simulation(TileMap.Open(24));
        sim.AddResource(1, ResourceType.Stone, 100);
        sim.AddResource(1, ResourceType.Wood, 100);

        Order(sim, Build(1, BuildingType.Wall, 5, 5));
        Check("a wall charges stone (100 - 5 = 95)", sim.Stockpile(1, ResourceType.Stone) == 95);

        Order(sim, Build(1, BuildingType.Gatehouse, 8, 8));
        Check("a gate charges wood and stone",
              sim.Stockpile(1, ResourceType.Wood) == 90 && sim.Stockpile(1, ResourceType.Stone) == 85);
        Check("both were placed", sim.Buildings.Count == 2);

        // Can't wall on top of a wall.
        int stoneBefore = sim.Stockpile(1, ResourceType.Stone);
        Order(sim, Build(1, BuildingType.Wall, 5, 5));
        Check("a wall on an occupied tile is refused and free",
              sim.Buildings.Count == 2 && sim.Stockpile(1, ResourceType.Stone) == stoneBefore);
    }

    static void TwoClientsAgreeOnWallsAndGates()
    {
        Console.WriteLine("\ntwo clients build and work gates identically:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.AddResource(1, ResourceType.Stone, 100);
            c.Sim.AddResource(1, ResourceType.Wood, 100);
        }

        var script = new Dictionary<int, Action>
        {
            [1] = () => { a.Issue(BuildIds(BuildingType.Wall, 5, 5)); b.Issue(BuildIds(BuildingType.Wall, 5, 5)); },
            [2] = () => { a.Issue(BuildIds(BuildingType.Gatehouse, 6, 5)); b.Issue(BuildIds(BuildingType.Gatehouse, 6, 5)); },
            [10] = () => { a.Issue(ToggleIds(2)); b.Issue(ToggleIds(2)); },   // gate is building id 2
            [40] = () => { a.Issue(ToggleIds(2)); b.Issue(ToggleIds(2)); },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 100; t++)
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

        Check($"StateChecksum identical on all 100 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both built the wall and gate", a.Sim.Buildings.Count == 2 && b.Sim.Buildings.Count == 2);
        var ga = a.Sim.Buildings.Find(x => x.Type == BuildingType.Gatehouse);
        var gb = b.Sim.Buildings.Find(x => x.Type == BuildingType.Gatehouse);
        Check("both agree on the gate's final open state", ga.Open == gb.Open);
        Check("both agree on that tile's passability",
              a.Sim.Map.Passable(6, 5) == b.Sim.Map.Passable(6, 5));
    }

    static void GateStateSurvivesARejoin()
    {
        Console.WriteLine("\na rejoin restores gate state and occupancy:");
        var host = new Simulation(TileMap.Open(24));
        host.AddResource(1, ResourceType.Stone, 100);
        host.AddResource(1, ResourceType.Wood, 100);
        var wall = host.PlaceBuilding(BuildingType.Wall, 1, 5, 5);
        var gate = host.PlaceBuilding(BuildingType.Gatehouse, 1, 6, 5);
        Toggle(host, 1, gate.Id);   // open the gate
        Check("the gate is open on the host", gate.Open && host.Map.Passable(6, 5));
        Check("the wall still blocks on the host", !host.Map.Passable(5, 5));

        var rejoiner = new Simulation(TileMap.Open(24));
        var units = new List<Unit>();
        foreach (var u in host.Units) units.Add(u.Clone());
        rejoiner.Restore(host.TickNumber, host.NextUnitId, host.RngState, units,
                         host.NextNodeId, host.NodeList, host.Stockpiles, host.DropOffs,
                         host.NextBuildingId, host.BuildingList, host.Designs);

        Check("the rebuilt sim hashes identically at the join",
              rejoiner.StateChecksum() == host.StateChecksum());
        Check("the OPEN gate comes back passable, not a solid wall",
              rejoiner.Map.Passable(6, 5));
        Check("the wall comes back blocking", !rejoiner.Map.Passable(5, 5));
        _ = wall;
    }

    // ---- helpers -----------------------------------------------------------

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });
    static void Toggle(Simulation sim, int owner, int gateId) =>
        Order(sim, new Command { Owner = owner, Type = CommandType.ToggleGate, TargetId = gateId });

    static Command Build(int owner, BuildingType type, int x, int y) => new Command
    { Owner = owner, Type = CommandType.Build, TargetId = (int)type, X = x, Y = y };

    static Command BuildIds(BuildingType type, int x, int y) => new Command
    { Type = CommandType.Build, TargetId = (int)type, X = x, Y = y };

    static Command ToggleIds(int gateId) => new Command
    { Type = CommandType.ToggleGate, TargetId = gateId };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
