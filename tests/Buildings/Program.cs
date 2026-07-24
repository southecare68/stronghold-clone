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
        AFootprintBlocksPathing();
        AKeepBecomesTheDropOff();
        ABarracksTrainsSoldiers();
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
        Console.WriteLine("\na barracks turns resources into soldiers:");
        var sim = new Simulation(TileMap.Open(48));
        Give(sim, 1, wood: 200, stone: 0);
        var barracks = sim.PlaceBuilding(BuildingType.Barracks, 1, 20, 20);

        int before = CountUnitsOf(sim, 1);
        Order(sim, Train(1, barracks.Id));
        Order(sim, Train(1, barracks.Id));
        Check("training charged wood (200 - 2*15 = 170)", sim.Stockpile(1, ResourceType.Wood) == 170);
        Check("two units are queued", barracks.TrainQueue.Count == 2);

        for (int i = 0; i < 200; i++) sim.Tick(Array.Empty<Command>());

        Check($"two soldiers were produced (had {before}, now {CountUnitsOf(sim, 1)})",
              CountUnitsOf(sim, 1) == before + 2);
        Check("the queue has drained", barracks.TrainQueue.Count == 0);
        Check("training with too little wood is refused",
              RefusedTrain(sim, barracks));
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

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

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

    static int CountUnitsOf(Simulation sim, int owner)
    {
        int n = 0;
        foreach (var u in sim.Units) if (u.Owner == owner) n++;
        return n;
    }

    static bool RefusedTrain(Simulation sim, Building barracks)
    {
        // Drain wood below cost, then a train order should place nothing new.
        int qBefore = barracks.TrainQueue.Count;
        int woodBefore = sim.Stockpile(1, ResourceType.Wood);
        // Spend down to under 15 by training while we can, then assert a refusal.
        while (sim.Stockpile(1, ResourceType.Wood) >= 15) Order(sim, Train(1, barracks.Id));
        int wood = sim.Stockpile(1, ResourceType.Wood);
        Order(sim, Train(1, barracks.Id));   // can't afford
        return sim.Stockpile(1, ResourceType.Wood) == wood && wood < 15 && woodBefore >= 0 && qBefore >= 0;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
