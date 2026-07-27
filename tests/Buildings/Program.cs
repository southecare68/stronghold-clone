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
        TheMillAndBakeryCostGrain();
        AFootprintBlocksPathing();
        AKeepBecomesTheDropOff();
        ABarracksTrainsSoldiers();
        DemolishRefundsFreesWorkerAndClearsGround();
        ATurretReplacesYourOwnWall();
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

    // The mill and bakery are gated behind a working grain supply: they cost
    // grain to build, not just wood and stone, so you must farm before you mill.
    static void TheMillAndBakeryCostGrain()
    {
        Console.WriteLine("\nthe mill and bakery cost grain, not just wood and stone:");
        var sim = new Simulation(TileMap.Open(48));

        // Wood and stone to spare, but no grain: the mill is refused.
        Give(sim, 1, wood: 200, stone: 200);
        Order(sim, Build(1, BuildingType.Mill, 10, 10));
        Check("a mill with no grain is refused", sim.Buildings.Count == 0);

        // With grain banked (from a farm), it goes up and grain is charged.
        sim.AddResource(1, ResourceType.Grain, 50);
        Order(sim, Build(1, BuildingType.Mill, 10, 10));
        Check("with grain in store, the mill is built", sim.Buildings.Count == 1);
        Check($"grain was charged (50 - 15 = 35, got {sim.Stockpile(1, ResourceType.Grain)})",
              sim.Stockpile(1, ResourceType.Grain) == 35);

        // The bakery is gated the same way.
        Order(sim, Build(1, BuildingType.Bakery, 20, 20));
        Check($"the bakery too spends grain (35 - 20 = 15, got {sim.Stockpile(1, ResourceType.Grain)})",
              sim.Stockpile(1, ResourceType.Grain) == 15 && sim.Buildings.Count == 2);
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

        // But an ENEMY wall is not yours to build over.
        Give(sim, 2, wood: 100, stone: 100);
        var foeWall = sim.PlaceBuilding(BuildingType.Wall, 2, 20, 20);
        Order(sim, Build(1, BuildingType.Turret, 20, 20));
        Check("an enemy wall cannot be replaced", sim.Buildings.Contains(foeWall) &&
              sim.Buildings.Find(b => b.Type == BuildingType.Turret && b.X == 20) == null);
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
