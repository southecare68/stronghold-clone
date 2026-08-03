// Buildings — placement, blocking, cost, production; deterministic and in sync.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Buildings — placement, blocking, cost, production\n");

        PlacementCostsAndValidates();
        TheMillAndBakeryCostNoGrain();
        AFootprintBlocksPathing();
        AKeepBecomesTheDropOff();
        ABarracksTrainsSoldiers();
        RallyPointMarchesRecruits();
        DemolishRefundsFreesWorkerAndClearsGround();
        AnIronMineWorksAnIronSeam();
        YouCanBuildAnywhereInYourTerritory();
        TheTerritoryBorderStaysPutAsYouBuild();
        ATurretReplacesYourOwnWall();
        ATurretBridgesAOneTileGapToTheWall();
        MoveOnlyBuildsNothing();
        TwoClientsAgreeOnBuildAndTrain();
        BuildingsSurviveARejoin();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void PlacementCostsAndValidates()
    {
        Console.WriteLine("placement is validated and paid for:");
        var sim = new Simulation(TileMap.Open(48));
        var worker = sim.SpawnUnit(1, 5, 5);

        // Too poor: a build with an empty stockpile places nothing.
        Order(sim, Build(1, BuildingType.Barracks, 10, 10));
        Check("a build you cannot afford is refused", sim.Buildings.Count == 0);

        // Give player 1 enough and try again.
        Give(sim, 1, wood: 100, stone: 100);
        Order(sim, Build(1, BuildingType.Barracks, 10, 10));
        Check("an affordable build is placed", sim.Buildings.Count == 1);
        Check("wood was charged (100 - 40 = 60)", sim.Stockpile(1, ResourceType.Wood) == 60);

        // Overlap: a second building on the same tiles is refused (and free).
        int woodBefore = sim.Stockpile(1, ResourceType.Wood);
        Order(sim, Build(1, BuildingType.Keep, 10, 10));
        Check("a build overlapping another is refused", sim.Buildings.Count == 1);
        Check("and a refused build costs nothing", sim.Stockpile(1, ResourceType.Wood) == woodBefore);

        // On water/rock: refused.
        var watery = new Simulation(TileMap.FromRows(
            "......",
            "..~~..",
            "..~~..",
            "......"));
        Give(watery, 1, wood: 100, stone: 100);
        Order(watery, Build(1, BuildingType.Barracks, 2, 1));
        Check("a build on water is refused", watery.Buildings.Count == 0);

        _ = worker;
    }

    // The mill and bakery cost timber and stone, NEVER grain — so the food chain can
    // keep growing even while a running mill grinds grain to flour as fast as it is
    // reaped. Costing grain once deadlocked expansion: grain could never stockpile to
    // the price of a second bakery, because the mill ate it first.
    static void TheMillAndBakeryCostNoGrain()
    {
        Console.WriteLine("\nthe mill and bakery cost timber and stone, never grain:");
        var sim = new Simulation(TileMap.Open(48));
        Give(sim, 1, wood: 500, stone: 500);   // no grain banked at all

        Order(sim, Build(1, BuildingType.Mill, 10, 10));
        Check("a mill builds on no grain", sim.Buildings.Count == 1);
        Order(sim, Build(1, BuildingType.Bakery, 20, 20));
        Check("so does a bakery", sim.Buildings.Count == 2);

        // The whole point: a SECOND bakery goes up with zero grain banked — the
        // deadlock can no longer block growing the food chain.
        Order(sim, Build(1, BuildingType.Bakery, 30, 30));
        Check("and a SECOND bakery too, grain or none",
              sim.Buildings.FindAll(b => b.Type == BuildingType.Bakery).Count == 2);
        Check($"no grain was spent ({sim.Stockpile(1, ResourceType.Grain)})",
              sim.Stockpile(1, ResourceType.Grain) == 0);
    }

    static void AFootprintBlocksPathing()
    {
        Console.WriteLine("\na footprint blocks movement:");
        var sim = new Simulation(TileMap.Open(24));

        // Before building: the straight route runs right through where the keep
        // will go. (PathFinder returns the raw tile route — smoothing lives in
        // Simulation — so this is a full tile line, not one leg.)
        var pf = new PathFinder(sim.Map);
        var path = new List<Tile>();
        Check("a clear path exists across open ground", pf.TryFindPath(2, 6, 20, 6, path) && path.Count > 0);
        bool crossedBefore = false;
        foreach (var t in path)
            if (t.X >= 10 && t.X <= 12 && t.Y >= 5 && t.Y <= 7) crossedBefore = true;
        Check("and it runs straight through the keep's future footprint", crossedBefore);

        // Drop a 3x3 keep squarely across that line.
        var b = sim.PlaceBuilding(BuildingType.Keep, 1, 10, 5);
        Check("the keep was placed", b != null);
        Check("its footprint tiles are now impassable",
              !sim.Map.Passable(10, 6) && !sim.Map.Passable(11, 6) && !sim.Map.Passable(12, 6));

        Check("a path still exists, detouring", pf.TryFindPath(2, 6, 20, 6, path) && path.Count > 0);
        foreach (var t in path)
            if (t.X >= 10 && t.X <= 12 && t.Y >= 5 && t.Y <= 7)
            { Check("the route never crosses the footprint", false); return; }
        Check("the route never crosses the footprint", true);
    }

    static void AKeepBecomesTheDropOff()
    {
        Console.WriteLine("\na keep anchors the economy drop-off:");
        var sim = new Simulation(TileMap.Open(48));
        Give(sim, 1, wood: 100, stone: 100);
        var worker = sim.SpawnUnit(1, 20, 20);
        var node = sim.SpawnNode(ResourceType.Wood, 26, 20, 100);

        // No drop-off yet: a gather order has nowhere to bank, so it is refused.
        Order(sim, Gather(worker, node));
        Check("without a keep, gathering is refused", worker.Job == Job.None);

        // Build a keep near the worker (but NOT on top of it — a footprint tile
        // is impassable, and a worker trapped inside could never leave). It
        // registers as the drop-off.
        var keep = sim.PlaceBuilding(BuildingType.Keep, 1, 15, 15);
        Order(sim, Gather(worker, node));
        Check("with a keep, the worker takes the job", worker.Job == Job.Gathering);

        for (int i = 0; i < 800; i++) sim.Tick(Array.Empty<Command>());
        Check("and wood ends up banked at the keep", sim.Stockpile(1, ResourceType.Wood) > 100);
        _ = keep;
    }

    // A rally point set on a barracks marches every recruit to it as it rolls off the
    // line; without one, a recruit just musters at the barracks gate.
    static void RallyPointMarchesRecruits()
    {
        Console.WriteLine("\na rally point marches new recruits to it:");
        var sim = new Simulation(TileMap.Open(64));
        sim.SetDropOff(1, 5, 5);
        Give(sim, 1, wood: 200, stone: 0);
        for (int i = 0; i < 3; i++) sim.SpawnPeasant(1);
        var barracks = sim.PlaceBuilding(BuildingType.Barracks, 1, 20, 20);

        Order(sim, new Command { Owner = 1, Seq = 1, Type = CommandType.SetRally, TargetId = barracks.Id, X = 45, Y = 45 });
        Check("the barracks holds the rally point", barracks.HasRally && barracks.RallyX == 45 && barracks.RallyY == 45);

        Order(sim, Train(1, barracks.Id));
        for (int i = 0; i < 400; i++) sim.Tick(Array.Empty<Command>());
        var soldier = sim.Units.Find(u => u.Owner == 1 && !u.IsPeasant);
        Check("a soldier was produced", soldier != null);
        if (soldier != null)
            Check($"and it marched to the rally point ({Fixed.ToInt(soldier.X)},{Fixed.ToInt(soldier.Y)})",
                  Math.Abs(Fixed.ToInt(soldier.X) - 45) <= 2 && Math.Abs(Fixed.ToInt(soldier.Y) - 45) <= 2);

        // A rally-less barracks: the recruit just musters at the gate.
        var plain = new Simulation(TileMap.Open(64));
        plain.SetDropOff(1, 5, 5);
        Give(plain, 1, wood: 200, stone: 0);
        for (int i = 0; i < 3; i++) plain.SpawnPeasant(1);
        var bar2 = plain.PlaceBuilding(BuildingType.Barracks, 1, 20, 20);
        Order(plain, Train(1, bar2.Id));
        for (int i = 0; i < 400; i++) plain.Tick(Array.Empty<Command>());
        var s2 = plain.Units.Find(u => u.Owner == 1 && !u.IsPeasant);
        Check("a rally-less recruit stays by the barracks",
              s2 != null && Math.Abs(Fixed.ToInt(s2.X) - 20) <= 4 && Math.Abs(Fixed.ToInt(s2.Y) - 20) <= 4);
    }

    static void ABarracksTrainsSoldiers()
    {
        Console.WriteLine("\na barracks arms peasants into soldiers:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 5, 5);                    // where seeded peasants appear
        Give(sim, 1, wood: 200, stone: 0);
        for (int i = 0; i < 4; i++) sim.SpawnPeasant(1);   // the manpower to arm
        var barracks = sim.PlaceBuilding(BuildingType.Barracks, 1, 20, 20);

        int pBefore = sim.PeasantCount(1);          // 4
        Order(sim, Train(1, barracks.Id));
        Order(sim, Train(1, barracks.Id));
        Check("training charged wood (200 - 2*15 = 170)", sim.Stockpile(1, ResourceType.Wood) == 170);
        Check("two units are queued", barracks.TrainQueue.Count == 2);

        for (int i = 0; i < 200; i++) sim.Tick(Array.Empty<Command>());

        Check($"two soldiers marched out ({CountSoldiers(sim, 1)})", CountSoldiers(sim, 1) == 2);
        Check($"and two peasants were armed to make them ({sim.PeasantCount(1)} of {pBefore})",
              sim.PeasantCount(1) == pBefore - 2);
        Check("the queue has drained", barracks.TrainQueue.Count == 0);

        // Arm the last two, emptying the pool.
        Order(sim, Train(1, barracks.Id));
        Order(sim, Train(1, barracks.Id));
        for (int i = 0; i < 200; i++) sim.Tick(Array.Empty<Command>());
        Check("all four peasants are now soldiers",
              sim.PeasantCount(1) == 0 && CountSoldiers(sim, 1) == 4);

        // No idle peasant left: training is refused even with wood to spare,
        // and nothing is charged.
        int woodNow = sim.Stockpile(1, ResourceType.Wood);
        Order(sim, Train(1, barracks.Id));
        Check("training with no idle peasant is refused (wood untouched)",
              sim.Stockpile(1, ResourceType.Wood) == woodNow && barracks.TrainQueue.Count == 0);

        // And the wood gate still bites: a fresh sim with a peasant but no wood.
        var poor = new Simulation(TileMap.Open(48));
        poor.SetDropOff(1, 5, 5);
        poor.SpawnPeasant(1);
        var bk = poor.PlaceBuilding(BuildingType.Barracks, 1, 20, 20);
        Order(poor, Train(1, bk.Id));
        Check("training with no wood is refused too", bk.TrainQueue.Count == 0);
    }

    static void MoveOnlyBuildsNothing()
    {
        Console.WriteLine("\nnothing here touches Checksum's world without a Build order:");
        // A move-only sim must have no buildings and an unchanged stockpile —
        // the same isolation that keeps SimParity's 0xB1A7A676 intact.
        var sim = new Simulation(TileMap.Open(24));
        var u = sim.SpawnUnit(1, 3, 3);
        Order(sim, Move(u, 20, 20));
        for (int i = 0; i < 200; i++) sim.Tick(Array.Empty<Command>());
        Check("no buildings appeared", sim.Buildings.Count == 0);
        Check("no map tile got blocked", sim.Map.Passable(10, 10));
    }

    static void TwoClientsAgreeOnBuildAndTrain()
    {
        Console.WriteLine("\ntwo clients build and train identically:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            Give(c.Sim, 1, wood: 300, stone: 100);
            c.Sim.SpawnUnit(1, 5, 5);          // id 1
            for (int i = 0; i < 2; i++) c.Sim.SpawnPeasant(1);   // manpower for the two soldiers
        }

        var script = new Dictionary<int, Action>
        {
            [1]  = () => { a.Issue(BuildIds(BuildingType.Barracks, 10, 10)); b.Issue(BuildIds(BuildingType.Barracks, 10, 10)); },
            [5]  = () => { a.Issue(TrainIds(1)); b.Issue(TrainIds(1)); },   // barracks id 1
            [6]  = () => { a.Issue(TrainIds(1)); b.Issue(TrainIds(1)); },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 400; t++)
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

        Check($"StateChecksum identical on all 400 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both placed the barracks", a.Sim.Buildings.Count == 1 && b.Sim.Buildings.Count == 1);
        Check("both trained the same number of soldiers",
              a.Sim.Units.Count == b.Sim.Units.Count && a.Sim.Units.Count == 3);
        Check("both charged the same wood", a.Sim.Stockpile(1, ResourceType.Wood) ==
              b.Sim.Stockpile(1, ResourceType.Wood));
    }

    static void BuildingsSurviveARejoin()
    {
        Console.WriteLine("\na rejoin rebuilds buildings and their blocking:");
        var host = new Simulation(TileMap.Open(48));
        Give(host, 1, wood: 300, stone: 100);
        host.PlaceBuilding(BuildingType.Keep, 1, 10, 10);
        for (int i = 0; i < 2; i++) host.SpawnPeasant(1);   // a peasant for the training to arm
        var barracks = host.PlaceBuilding(BuildingType.Barracks, 1, 20, 20);
        Order(host, Train(1, barracks.Id));
        for (int i = 0; i < 30; i++) host.Tick(Array.Empty<Command>());

        var rejoiner = new Simulation(TileMap.Open(48));
        var units = new List<Unit>();
        foreach (var u in host.Units) units.Add(u.Clone());
        rejoiner.Restore(host.TickNumber, host.NextUnitId, host.RngState, units,
                         host.NextNodeId, host.NodeList, host.Stockpiles, host.DropOffs,
                         host.NextBuildingId, host.BuildingList, host.Designs);

        Check("the rebuilt sim hashes identically at the join",
              rejoiner.StateChecksum() == host.StateChecksum());
        Check("the buildings came across", rejoiner.Buildings.Count == 2);
        Check("and their footprints re-block the rejoiner's map",
              !rejoiner.Map.Passable(11, 11) && !rejoiner.Map.Passable(21, 21));

        int desyncs = 0;
        for (int i = 0; i < 300; i++)
        {
            host.Tick(Array.Empty<Command>());
            rejoiner.Tick(Array.Empty<Command>());
            if (host.StateChecksum() != rejoiner.StateChecksum()) desyncs++;
        }
        Check("no divergence over 300 ticks after the rejoin (incl. the training)", desyncs == 0);
        Check("both finished with the same unit count", host.Units.Count == rejoiner.Units.Count);
    }

    // ---- helpers -----------------------------------------------------------

    // Demolishing gives back half the cost, returns the worker to the labour pool,
    // clears the ground — and never applies to the keep.
    static void DemolishRefundsFreesWorkerAndClearsGround()
    {
        Console.WriteLine("\ndemolishing refunds, frees the worker, and clears the ground:");
        var sim = new Simulation(TileMap.Open(48));
        Give(sim, 1, wood: 100, stone: 100);
        var keep = sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);   // sets the drop-off (free, setup path)
        sim.SpawnNode(ResourceType.Wood, 12, 12, 100);
        sim.SpawnPeasant(1);                                        // idle, at the keep

        Order(sim, Build(1, BuildingType.WoodcutterHut, 10, 10));   // charges 15 wood
        var hut = sim.Buildings.Find(b => b.Type == BuildingType.WoodcutterHut);
        Check("the hut was built", hut != null);
        for (int i = 0; i < 5; i++) sim.Tick(new List<Command>());  // let it hire the idle peasant
        Check("the hut took on a worker", hut.WorkerId != 0 && sim.IdlePeasantCount(1) == 0);

        int woodBefore = sim.Stockpile(1, ResourceType.Wood);
        Order(sim, Demolish(1, hut.Id));
        Check("the hut is gone", sim.Buildings.Find(b => b.Type == BuildingType.WoodcutterHut) == null);
        Check("its footprint is walkable again", sim.Map.Passable(10, 10));
        Check($"half the 15-wood cost is refunded (+7, got {sim.Stockpile(1, ResourceType.Wood) - woodBefore})",
              sim.Stockpile(1, ResourceType.Wood) == woodBefore + 7);
        Check("the worker rejoined the idle pool", sim.IdlePeasantCount(1) == 1);

        // The keep is not demolishable — that would be a defeat, not a refund.
        int woodNow = sim.Stockpile(1, ResourceType.Wood);
        Order(sim, Demolish(1, keep.Id));
        Check("the keep cannot be demolished", sim.Buildings.Contains(keep) &&
              sim.Stockpile(1, ResourceType.Wood) == woodNow);

        // You cannot demolish someone else's building.
        var enemyHut = sim.PlaceBuilding(BuildingType.Barracks, 2, 30, 30);
        Order(sim, Demolish(1, enemyHut.Id));
        Check("an enemy's building is not yours to raze", sim.Buildings.Contains(enemyHut));

        // A WALL can be demolished too — it refunds its stone and turns its garrison
        // back into a field unit.
        Give(sim, 1, wood: 0, stone: 20);
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 6, 6);
        var soldier = sim.SpawnUnit(1, 6, 8);
        soldier.GarrisonId = wall.Id;
        int stoneBefore = sim.Stockpile(1, ResourceType.Stone);
        Order(sim, Demolish(1, wall.Id));
        Check("a wall is demolished", sim.Buildings.Find(b => b.Type == BuildingType.Wall) == null);
        Check($"half its 5 stone comes back (+2, got {sim.Stockpile(1, ResourceType.Stone) - stoneBefore})",
              sim.Stockpile(1, ResourceType.Stone) == stoneBefore + 2);
        Check("its garrison drops back to the field", soldier.GarrisonId == 0);
    }

    // A tower stands IN the wall line, so raising a turret on one of your own wall
    // segments replaces it rather than being refused as "tile blocked" — the fix
    // for "the turret won't go next to the wall, it's red".
    static void ATurretReplacesYourOwnWall()
    {
        Console.WriteLine("\na turret raised on your own wall replaces that segment:");
        var sim = new Simulation(TileMap.Open(48));
        Give(sim, 1, wood: 100, stone: 100);
        var wall = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 10);
        int wallId = wall.Id;
        var soldier = sim.SpawnUnit(1, 10, 12);
        soldier.GarrisonId = wall.Id;

        Order(sim, Build(1, BuildingType.Turret, 10, 10));    // aim a turret at your own wall
        var turret = sim.Buildings.Find(b => b.Type == BuildingType.Turret);
        Check("the turret was raised on the tile", turret != null && turret.X == 10 && turret.Y == 10);
        Check("the wall segment it stood on is gone", sim.Buildings.Find(b => b.Id == wallId) == null);
        Check("the tile stays blocked (now the turret)", !sim.Map.Passable(10, 10));
        Check("the old wall's garrison was turned out", soldier.GarrisonId == 0);

        // A GATEHOUSE drops into a finished wall the same way — a gateway sits in
        // the line too, so it replaces the segment it is aimed at.
        var wall2 = sim.PlaceBuilding(BuildingType.Wall, 1, 10, 14);
        Order(sim, Build(1, BuildingType.Gatehouse, 10, 14));
        Check("a gatehouse replaces your own wall too",
              sim.Buildings.Find(b => b.Id == wall2.Id) == null &&
              sim.Buildings.Find(b => b.Type == BuildingType.Gatehouse && b.X == 10 && b.Y == 14) != null);

        // But an ENEMY wall is not yours to build over.
        Give(sim, 2, wood: 100, stone: 100);
        var foeWall = sim.PlaceBuilding(BuildingType.Wall, 2, 20, 20);
        Order(sim, Build(1, BuildingType.Turret, 20, 20));
        Check("an enemy wall cannot be replaced", sim.Buildings.Contains(foeWall) &&
              sim.Buildings.Find(b => b.Type == BuildingType.Turret && b.X == 20) == null);
    }

    // A turret dropped one tile past the end of a wall used to leave an open tile
    // an invader could walk through, and the player had to notice and wall it. Now
    // the turret bridges that single gap itself, so the line stays continuous — the
    // fix for "the turret at the end of the wall creates a gap."
    static void ATurretBridgesAOneTileGapToTheWall()
    {
        Console.WriteLine("\na turret one tile off the wall bridges the gap:");
        var sim = new Simulation(TileMap.Open(48));
        Give(sim, 1, wood: 200, stone: 200);
        for (int x = 10; x <= 15; x++) sim.PlaceBuilding(BuildingType.Wall, 1, x, 10);  // wall ends at 15

        Order(sim, Build(1, BuildingType.Turret, 17, 10));      // one empty tile (16) between
        Check("the turret was raised", sim.Buildings.Find(b => b.Type == BuildingType.Turret && b.X == 17) != null);
        Check("the one-tile gap was bridged with a wall",
              sim.Buildings.Find(b => b.Type == BuildingType.Wall && b.X == 16 && b.Y == 10) != null);
        Check("so the line is solid — the gap tile is now blocked", !sim.Map.Passable(16, 10));

        // Order does not matter: place the TURRET first, then wall up to one tile
        // short of it, and the last gap still closes when that wall goes down.
        var s1 = new Simulation(TileMap.Open(48));
        Give(s1, 1, wood: 200, stone: 200);
        Order(s1, Build(1, BuildingType.Turret, 17, 10));       // tower first
        for (int x = 10; x <= 15; x++) Order(s1, Build(1, BuildingType.Wall, x, 10));  // wall ends at 15, gap at 16
        Check("the turret-then-wall gap was bridged too",
              !s1.Map.Passable(16, 10) &&
              s1.Buildings.Find(b => b.Type == BuildingType.Wall && b.X == 16 && b.Y == 10) != null);

        // Only a SINGLE open tile is a connector — a two-tile gap is left for the
        // player to wall deliberately, so a turret never sprouts a long free wall.
        var s2 = new Simulation(TileMap.Open(48));
        Give(s2, 1, wood: 200, stone: 200);
        for (int x = 10; x <= 15; x++) s2.PlaceBuilding(BuildingType.Wall, 1, x, 10);
        Order(s2, Build(1, BuildingType.Turret, 18, 10));       // two empty tiles (16, 17)
        Check("a two-tile gap is not auto-bridged",
              s2.Map.Passable(16, 10) && s2.Map.Passable(17, 10));

        // Two plain WALLS a tile apart are NOT joined — a one-tile opening between
        // wall runs (a gateway to come) must survive, since no turret is involved.
        var s3 = new Simulation(TileMap.Open(48));
        Give(s3, 1, wood: 200, stone: 200);
        for (int x = 10; x <= 13; x++) s3.PlaceBuilding(BuildingType.Wall, 1, x, 10);
        Order(s3, Build(1, BuildingType.Wall, 15, 10));         // one empty tile (14) between two wall runs
        Check("a gap between two plain walls is preserved", s3.Map.Passable(14, 10));
    }

    // Your territory is land you hold, so you may build across all of it — even the
    // parts your units have not scouted — while unseen ground OUTSIDE it stays off
    // limits. The fix for "I can't build to the edge of my territory."
    static void YouCanBuildAnywhereInYourTerritory()
    {
        Console.WriteLine("\nyou can build anywhere in your own territory, scouted or not:");
        var sim = new Simulation(TileMap.Open(64)) { FogEnabled = true };
        Give(sim, 1, wood: 200, stone: 200);
        sim.PlaceBuilding(BuildingType.Keep, 1, 10, 10);
        sim.SpawnNode(ResourceType.Wood, 10, 25, 100);   // a home patch, so the territory reaches out
        for (int i = 0; i < 3; i++) sim.Tick(Array.Empty<Command>());

        var home = sim.HomeRect(1);
        bool InHome(int x, int y) => home.HasValue &&
            x >= home.Value.minX && x <= home.Value.maxX && y >= home.Value.minY && y <= home.Value.maxY;

        int fx = 10, fy = 34;   // deep in the territory, far beyond the keep's sight
        Check("the spot is genuinely unexplored", !sim.Fog.IsExplored(1, fx, fy));
        Check("but it is inside my territory", InHome(fx, fy) && InHome(fx + 1, fy + 1));
        Order(sim, Build(1, BuildingType.Barracks, fx, fy));
        Check("so the barracks goes up there", sim.Buildings.Find(b => b.Type == BuildingType.Barracks && b.X == fx) != null);

        int ox = 55, oy = 55;   // unseen ground well outside the territory
        Check("that far tile is unexplored and off my land", !sim.Fog.IsExplored(1, ox, oy) && !InHome(ox, oy));
        Order(sim, Build(1, BuildingType.Barracks, ox, oy));
        Check("a build on unseen ground outside the territory is refused",
              sim.Buildings.Find(b => b.Type == BuildingType.Barracks && b.X == ox) == null);
    }

    // The home border is pinned to the KEEP, not the live spread of your buildings,
    // so raising a wall along the frontier must NOT push the border out. Before this
    // was fixed the border was drawn from the bounding box of ALL your buildings, so
    // each edge building widened it and the line crept outward one wall at a time.
    static void TheTerritoryBorderStaysPutAsYouBuild()
    {
        Console.WriteLine("\nthe territory border stays put as you build along it:");
        var sim = new Simulation(TileMap.Open(64)) { FogEnabled = true };
        Give(sim, 1, wood: 500, stone: 500);
        sim.PlaceBuilding(BuildingType.Keep, 1, 24, 24);
        for (int i = 0; i < 3; i++) sim.Tick(Array.Empty<Command>());

        var before = sim.HomeRect(1);
        Check("the keep alone stakes out a territory", before.HasValue);

        // A wall on the far frontier — the extreme corner of the home rectangle.
        int ex = before.Value.maxX, ey = before.Value.maxY;
        Order(sim, Build(1, BuildingType.Wall, ex, ey));
        Check($"the wall goes up on the frontier ({ex},{ey})",
              sim.Buildings.Find(b => b.Type == BuildingType.Wall && b.X == ex && b.Y == ey) != null);
        Check($"and the border did not move ({Fmt(before)} -> {Fmt(sim.HomeRect(1))})",
              SameRect(before, sim.HomeRect(1)));

        // A second wall on the opposite frontier — still no shift.
        int wx = before.Value.minX, wy = before.Value.maxY;
        Order(sim, Build(1, BuildingType.Wall, wx, wy));
        Check($"a second frontier wall ({wx},{wy}) still leaves the border where it was",
              SameRect(before, sim.HomeRect(1)));
    }

    static bool SameRect((int minX, int minY, int maxX, int maxY)? a, (int minX, int minY, int maxX, int maxY)? b) =>
        a.HasValue && b.HasValue && a.Value.minX == b.Value.minX && a.Value.minY == b.Value.minY
        && a.Value.maxX == b.Value.maxX && a.Value.maxY == b.Value.maxY;

    static string Fmt((int minX, int minY, int maxX, int maxY)? r) =>
        r.HasValue ? $"[{r.Value.minX},{r.Value.minY}..{r.Value.maxX},{r.Value.maxY}]" : "none";

    // An iron mine is a harvester like the quarry — it hires an idle peasant who
    // digs ore from the nearest iron seam and hauls it to the drop-off, so the iron
    // stockpile grows on its own.
    static void AnIronMineWorksAnIronSeam()
    {
        Console.WriteLine("\nan iron mine works an iron seam:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 4, 4);          // sets the drop-off
        sim.SpawnNode(ResourceType.Iron, 12, 12, 100);
        var mine = sim.PlaceBuilding(BuildingType.IronMine, 1, 9, 9);   // beside the ore
        sim.SpawnPeasant(1);                                     // an idle hand to hire

        for (int i = 0; i < 6; i++) sim.Tick(Array.Empty<Command>());
        Check("the mine took on a worker", mine.WorkerId != 0);

        int iron0 = sim.Stockpile(1, ResourceType.Iron);
        for (int i = 0; i < 400; i++) sim.Tick(Array.Empty<Command>());
        Check($"iron was mined and banked ({iron0} -> {sim.Stockpile(1, ResourceType.Iron)})",
              sim.Stockpile(1, ResourceType.Iron) > iron0);
    }

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Demolish(int owner, int buildingId) => new Command
    { Owner = owner, Type = CommandType.Demolish, TargetId = buildingId };

    static void Give(Simulation sim, int owner, int wood = 0, int stone = 0, int food = 0)
    {
        sim.AddResource(owner, ResourceType.Wood, wood);
        sim.AddResource(owner, ResourceType.Stone, stone);
        sim.AddResource(owner, ResourceType.Food, food);
    }

    static Command Move(Unit u, int x, int y) => new Command
    { Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y };

    static Command Gather(Unit u, ResourceNode node) => new Command
    { Owner = u.Owner, Type = CommandType.Gather, UnitIds = new[] { u.Id }, TargetId = node.Id };

    static Command Build(int owner, BuildingType type, int x, int y) => new Command
    { Owner = owner, Type = CommandType.Build, TargetId = (int)type, X = x, Y = y };

    static Command Train(int owner, int buildingId) => new Command
    { Owner = owner, Type = CommandType.Train, TargetId = buildingId };

    static Command BuildIds(BuildingType type, int x, int y) => new Command
    { Type = CommandType.Build, TargetId = (int)type, X = x, Y = y };

    static Command TrainIds(int buildingId) => new Command
    { Type = CommandType.Train, TargetId = buildingId };

    // Soldiers = non-peasant units (a trained unit is no longer a peasant).
    static int CountSoldiers(Simulation sim, int owner)
    {
        int n = 0;
        foreach (var u in sim.Units) if (u.Owner == owner && !u.IsPeasant) n++;
        return n;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
