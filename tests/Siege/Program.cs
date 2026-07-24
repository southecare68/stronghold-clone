// Siege — soldiers batter buildings down; the rubble stops blocking.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Siege — destructible buildings\n");

        AWallIsBreached();
        BreachingOpensTheRoute();
        OnlyEnemyBuildingsCanBeSieged();
        RazingAKeepRemovesItsDropOff();
        MoveOnlyLeavesBuildingsAlone();
        TwoClientsAgreeOnTheSiege();
        SiegeSurvivesARejoin();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void AWallIsBreached()
    {
        Console.WriteLine("soldiers batter a wall down:");
        var sim = new Simulation(TileMap.Open(24));
        var wall = sim.PlaceBuilding(BuildingType.Wall, 2, 10, 10);   // enemy (player 2) wall
        var s1 = sim.SpawnUnit(1, 8, 10);
        var s2 = sim.SpawnUnit(1, 8, 11);

        int startHp = wall.Hp;
        Order(sim, AttackBuilding(new[] { s1, s2 }, wall));
        Check("the soldiers took a siege target",
              s1.TargetBuildingId == wall.Id && s2.TargetBuildingId == wall.Id);

        int fellAt = -1;
        for (int i = 0; i < 600 && sim.Buildings.Count > 0; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (fellAt < 0 && sim.Buildings.Count == 0) fellAt = sim.TickNumber;
        }
        Check($"the wall's HP was being chipped down (started {startHp})", startHp == 200);
        Check($"the wall was destroyed (in ~{fellAt} ticks)", sim.Buildings.Count == 0);
        Check("the RNG advanced (siege rolls damage)", sim.RngState != new Simulation().RngState);

        // The besiegers notice their target is gone on the NEXT tick (destruction
        // happens after the combat phase), so give them one.
        sim.Tick(Array.Empty<Command>());
        Check("the besiegers clear their target once it is gone",
              s1.TargetBuildingId == 0 && s2.TargetBuildingId == 0);
    }

    static void BreachingOpensTheRoute()
    {
        Console.WriteLine("\nbreaching a wall opens the way through:");
        var sim = new Simulation(TileMap.FromRows(
            "##########",
            "..........",
            "##########"));
        var wall = sim.PlaceBuilding(BuildingType.Wall, 2, 5, 1);
        var pf = new PathFinder(sim.Map);
        var path = new List<Tile>();
        Check("the wall seals the corridor", !pf.TryFindPath(0, 1, 9, 1, path));

        var s1 = sim.SpawnUnit(1, 1, 1);
        var s2 = sim.SpawnUnit(1, 2, 1);
        Order(sim, AttackBuilding(new[] { s1, s2 }, wall));
        for (int i = 0; i < 800 && sim.Buildings.Count > 0; i++) sim.Tick(Array.Empty<Command>());

        Check("the wall is down", sim.Buildings.Count == 0);
        Check("its tile is passable rubble now", sim.Map.Passable(5, 1));
        Check("and the corridor is open again", pf.TryFindPath(0, 1, 9, 1, path));
    }

    static void OnlyEnemyBuildingsCanBeSieged()
    {
        Console.WriteLine("\nyou can't besiege your own buildings:");
        var sim = new Simulation(TileMap.Open(24));
        var ownWall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10);   // player 1's own
        var s1 = sim.SpawnUnit(1, 8, 10);

        Order(sim, AttackBuilding(new[] { s1 }, ownWall));
        Check("an order against your own wall is ignored", s1.TargetBuildingId == 0);

        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check("and your wall is untouched", sim.Buildings.Count == 1 && ownWall.Hp == ownWall.MaxHp);
    }

    static void RazingAKeepRemovesItsDropOff()
    {
        Console.WriteLine("\nrazing a keep removes its drop-off:");
        var sim = new Simulation(TileMap.Open(32));
        var keep = sim.PlaceBuilding(BuildingType.Keep, 2, 14, 14);   // enemy keep = a drop-off
        Check("the keep registered a drop-off for its owner",
              sim.DropOffs.ContainsKey(2));

        // A big siege party so a 600-HP keep falls in the tick budget.
        var attackers = new List<Unit>();
        for (int i = 0; i < 8; i++) attackers.Add(sim.SpawnUnit(1, 10 + i % 4, 12));
        Order(sim, AttackBuilding(attackers.ToArray(), keep));

        for (int i = 0; i < 1500 && sim.Buildings.Count > 0; i++) sim.Tick(Array.Empty<Command>());
        Check("the keep was razed", sim.Buildings.Count == 0);
        Check("and its drop-off is gone", !sim.DropOffs.ContainsKey(2));
    }

    static void MoveOnlyLeavesBuildingsAlone()
    {
        Console.WriteLine("\nno siege without an order:");
        var sim = new Simulation(TileMap.Open(24));
        var wall = sim.PlaceBuilding(BuildingType.Wall, 2, 10, 10);
        var u = sim.SpawnUnit(1, 8, 10);
        Order(sim, Move(u, 12, 10));   // walk right past the wall, no attack
        for (int i = 0; i < 300 && u.HasPath; i++) sim.Tick(Array.Empty<Command>());
        Check("a passing unit does not damage the wall",
              sim.Buildings.Count == 1 && wall.Hp == wall.MaxHp);
    }

    static void TwoClientsAgreeOnTheSiege()
    {
        Console.WriteLine("\ntwo clients batter in sync:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.PlaceBuilding(BuildingType.Wall, 2, 12, 10);   // building id 1
            c.Sim.SpawnUnit(1, 9, 10);                            // unit id 1
            c.Sim.SpawnUnit(1, 9, 11);                            // unit id 2
        }

        var script = new Dictionary<int, Action>
        {
            [1] = () => { a.Issue(AttackBuildingIds(new[] { 1, 2 }, 1)); b.Issue(AttackBuildingIds(new[] { 1, 2 }, 1)); },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 700; t++)
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
        Check($"StateChecksum identical on all 700 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both agree the wall fell", a.Sim.Buildings.Count == 0 && b.Sim.Buildings.Count == 0);
        Check("both agree that tile is passable now",
              a.Sim.Map.Passable(12, 10) && b.Sim.Map.Passable(12, 10));
    }

    static void SiegeSurvivesARejoin()
    {
        Console.WriteLine("\na rejoin carries building HP and the siege:");
        var host = new Simulation(TileMap.Open(24));
        var wall = host.PlaceBuilding(BuildingType.Wall, 2, 12, 10);
        var s1 = host.SpawnUnit(1, 9, 10);
        Order(host, AttackBuilding(new[] { s1 }, wall));
        for (int i = 0; i < 120; i++) host.Tick(Array.Empty<Command>());
        Check("the wall is damaged but standing", wall.Hp < wall.MaxHp && wall.Hp > 0);

        var rejoiner = new Simulation(TileMap.Open(24));
        var units = new List<Unit>();
        foreach (var u in host.Units) units.Add(u.Clone());
        rejoiner.Restore(host.TickNumber, host.NextUnitId, host.RngState, units,
                         host.NextNodeId, host.NodeList, host.Stockpiles, host.DropOffs,
                         host.NextBuildingId, host.BuildingList);

        Check("the rebuilt sim hashes identically at the join",
              rejoiner.StateChecksum() == host.StateChecksum());
        Check("the wall's damage came across",
              rejoiner.Buildings.Count == 1 && rejoiner.Buildings[0].Hp == wall.Hp);

        int desyncs = 0;
        for (int i = 0; i < 400; i++)
        {
            host.Tick(Array.Empty<Command>());
            rejoiner.Tick(Array.Empty<Command>());
            if (host.StateChecksum() != rejoiner.StateChecksum()) desyncs++;
        }
        Check("no divergence over 400 ticks after the rejoin (through the breach)", desyncs == 0);
        Check("both agree on whether the wall still stands",
              host.Buildings.Count == rejoiner.Buildings.Count);
    }

    // ---- helpers -----------------------------------------------------------

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Move(Unit u, int x, int y) => new Command
    { Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y };

    static Command AttackBuilding(Unit[] us, Building b)
    {
        var ids = new int[us.Length];
        for (int i = 0; i < us.Length; i++) ids[i] = us[i].Id;
        return new Command { Owner = us[0].Owner, Type = CommandType.AttackBuilding, UnitIds = ids, TargetId = b.Id };
    }

    static Command AttackBuildingIds(int[] units, int buildingId) => new Command
    { Type = CommandType.AttackBuilding, UnitIds = units, TargetId = buildingId };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
