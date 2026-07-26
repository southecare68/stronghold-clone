// FoodChain — farm → grain → mill → flour → bakery → bread.
//
// The farm is a work building like the woodcutter's hut, pointed at a wheat
// field it plants for itself; its farmer reaps grain and hauls it home. The mill
// and bakery are workshops: each turns a batch of one banked good into another
// every interval, but only while its input is on hand. Bread is the goal (Food).
//
// As with the wood chain, the failures that bite are determinism ones — a farm
// that sows its field on a different tile, or a workshop that fires a batch a
// tick apart, desyncs two machines a moment later — so the two-client check is
// the point. The rest pins down that the chain flows and that each step waits on
// its supplier instead of minting goods from nothing.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("FoodChain — farm → mill → bakery\n");

        NoFarmMeansNoGrain();
        AFarmPlantsAFieldAndGrainFlows();
        AFarmReplantsItsFieldSoItNeverRunsDry();
        AMillGrindsGrainIntoFlour();
        AMillWithNoGrainStaysIdle();
        ABakeryBakesFlourIntoBread();
        TheWholeChainTurnsAnEmptyLarderIntoFood();
        RazingTheFarmStopsItsFarmer();
        TwoClientsAgreeOnTheFoodChain();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // Opt-in: with no food buildings, grain and flour never leave zero and the
    // world is exactly what it was before the chain existed.
    static void NoFarmMeansNoGrain()
    {
        Console.WriteLine("no farm, no grain:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        sim.SpawnUnit(1, 6, 6);
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check("no grain appeared from nowhere", sim.Stockpile(1, ResourceType.Grain) == 0);
        Check("no flour either", sim.Stockpile(1, ResourceType.Flour) == 0);
        Check("and no field was sown", NodesOfType(sim, ResourceType.Grain) == 0);
    }

    // Placing a farm alone breeds a farmer, sows a field, and grain begins banking
    // with no orders given — the whole self-running cycle, on a bare grass map.
    static void AFarmPlantsAFieldAndGrainFlows()
    {
        Console.WriteLine("\na farm sows a field and grain flows:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);            // the drop-off
        Seed(sim, 1, 2);                                          // a couple of idle peasants

        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        Check("a wheat field was sown at once", NodesOfType(sim, ResourceType.Grain) >= 1);
        Check("empty farm has no worker yet", farm.WorkerId == 0);

        Settle(sim);                                             // it hires a farmer
        Check("the farm took on a farmer", farm.WorkerId != 0);
        Check("its worker is a peasant on the job (Job.Working)",
              Find(sim, farm.WorkerId)?.Job == Job.Working);

        Check("nothing banked yet", sim.Stockpile(1, ResourceType.Grain) == 0);
        for (int i = 0; i < 800; i++) sim.Tick(Array.Empty<Command>());
        Check($"grain is accumulating on its own ({sim.Stockpile(1, ResourceType.Grain)})",
              sim.Stockpile(1, ResourceType.Grain) > 0);
    }

    // A farm is renewable: reap far more grain than a single field holds, and it
    // keeps coming — the farm sows a fresh field each time the last is cut down.
    static void AFarmReplantsItsFieldSoItNeverRunsDry()
    {
        Console.WriteLine("\na farm replants its field and never runs dry:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 2);
        var store = sim.PlaceBuilding(BuildingType.Storehouse, 1, 15, 16);   // clear of the 3x3 farm
        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        Check("farm and storehouse both fit", store != null && farm != null);

        // One field holds 240 grain; run long enough to reap well past that. (No
        // bakery here, so no food and no population growth — grain just banks.)
        for (int i = 0; i < 6000; i++) sim.Tick(Array.Empty<Command>());
        int banked = sim.Stockpile(1, ResourceType.Grain);
        Check($"reaped more than a single field's worth ({banked} > 240)", banked > 240);
        Check("and a field is still standing to be cut",
              NodesOfType(sim, ResourceType.Grain) >= 1);
    }

    // A mill grinds banked grain into flour, one batch at a time, and takes only
    // grain — it does not conjure flour where there was no grain to grind.
    static void AMillGrindsGrainIntoFlour()
    {
        Console.WriteLine("\na mill grinds grain into flour:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 17, 17);          // miller spawns here; no keep => no population growth
        sim.AddResource(1, ResourceType.Grain, 40);
        Seed(sim, 1, 1);                    // a miller to man the mill
        sim.PlaceBuilding(BuildingType.Mill, 1, 20, 20);

        for (int i = 0; i < 400; i++) sim.Tick(Array.Empty<Command>());
        int grain = sim.Stockpile(1, ResourceType.Grain);
        int flour = sim.Stockpile(1, ResourceType.Flour);
        Check($"flour was produced ({flour})", flour > 0);
        Check($"grain was consumed to make it ({grain} left of 40)", grain < 40);
        Check("grain in == flour out (1:1, nothing lost or minted)", (40 - grain) == flour);
    }

    // A mill with no grain grinds nothing — the step waits on its supplier rather
    // than producing flour from an empty bin.
    static void AMillWithNoGrainStaysIdle()
    {
        Console.WriteLine("\na manned mill with no grain stays idle:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 17, 17);
        Seed(sim, 1, 1);                    // fully manned — but there is no grain
        sim.PlaceBuilding(BuildingType.Mill, 1, 20, 20);
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check("no flour from an empty bin", sim.Stockpile(1, ResourceType.Flour) == 0);
    }

    // A bakery bakes banked flour into bread (Food), consuming flour as it goes.
    static void ABakeryBakesFlourIntoBread()
    {
        Console.WriteLine("\na bakery bakes flour into bread:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 17, 17);          // baker spawns here; no keep => food is not spent on population
        sim.AddResource(1, ResourceType.Flour, 40);
        Seed(sim, 1, 1);                    // a baker to man the bakery
        sim.PlaceBuilding(BuildingType.Bakery, 1, 20, 20);

        for (int i = 0; i < 400; i++) sim.Tick(Array.Empty<Command>());
        int flour = sim.Stockpile(1, ResourceType.Flour);
        int food = sim.Stockpile(1, ResourceType.Food);
        Check($"bread (Food) was baked ({food})", food > 0);
        Check($"flour was consumed to bake it ({flour} left of 40)", flour < 40);
    }

    // End to end on an empty larder: a farm, a mill, and a bakery, staffed by a
    // starting workforce, must turn wheat into bread and — the whole point now —
    // bread into new peasants, all by themselves.
    static void TheWholeChainTurnsAnEmptyLarderIntoFood()
    {
        Console.WriteLine("\nthe whole chain turns wheat into bread into peasants:");
        var sim = new Simulation(TileMap.Open(64));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 3);                                                     // a farmer, a miller, a baker
        int seeded = Peasants(sim, 1);
        var store = sim.PlaceBuilding(BuildingType.Storehouse, 1, 15, 16);   // grain banks close to the field
        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        var mill = sim.PlaceBuilding(BuildingType.Mill, 1, 30, 30);
        var bakery = sim.PlaceBuilding(BuildingType.Bakery, 1, 34, 30);
        Check("all four buildings fit", store != null && farm != null && mill != null && bakery != null);

        Check("larder starts empty", sim.Stockpile(1, ResourceType.Food) == 0);
        for (int i = 0; i < 5000; i++) sim.Tick(Array.Empty<Command>());

        // Food is now SPENT on population, so the payoff is more peasants, not a
        // rising food pile — the chain fed and grew the workforce.
        Check($"the workforce grew past the {seeded} it started with ({Peasants(sim, 1)})",
              Peasants(sim, 1) > seeded);
    }

    // A razed farm lets its farmer go — the field it planted stops being worked,
    // exactly as a razed hut releases its woodcutter.
    static void RazingTheFarmStopsItsFarmer()
    {
        Console.WriteLine("\nrazing the farm stops its farmer:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 1);
        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        for (int i = 0; i < 60; i++) sim.Tick(Array.Empty<Command>());
        var farmer = Find(sim, farm.WorkerId);
        Check("the farm has a working farmer", farmer != null && farmer.Job == Job.Working);

        farm.Hp = 0;
        sim.Tick(Array.Empty<Command>());     // RemoveDestroyedBuildings runs
        Check("the razed farm is gone", sim.BuildingList.Count == 1);   // just the keep
        Check("its farmer rejoined the idle pool (Job.None, still a peasant)",
              farmer.Job == Job.None && farmer.IsPeasant);
    }

    // The one that matters most: the whole chain, computed twice, must agree on
    // every tick — the field tiles, the haul, and each workshop batch.
    static void TwoClientsAgreeOnTheFoodChain()
    {
        Console.WriteLine("\ntwo clients agree on the food chain:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(64));
        var b = new Client(2, net, TileMap.Open(64));
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
            Seed(c.Sim, 1, 5);                                           // workforce for two farms + mill + bakery, plus growth
            c.Sim.PlaceBuilding(BuildingType.Storehouse, 1, 15, 16);
            var f1 = c.Sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
            var f2 = c.Sim.PlaceBuilding(BuildingType.Farm, 1, 26, 26);   // two farms, well clear of each other
            c.Sim.PlaceBuilding(BuildingType.Mill, 1, 40, 30);
            c.Sim.PlaceBuilding(BuildingType.Bakery, 1, 44, 30);
            if (f1 == null || f2 == null) Check("both farms fit", false);
        }

        int desyncs = 0, first = -1;
        for (int t = 0; t < 1500; t++)
        {
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 1500 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        // The chain ran end to end: bread bred peasants past the 5 each started
        // with. (Both agree by the checksum above, so checking one is enough.)
        Check($"and the workforce grew from the chain ({Peasants(a.Sim, 1)} on A)",
              Peasants(a.Sim, 1) > 5);
    }

    // ---- helpers -----------------------------------------------------------

    static int NodesOfType(Simulation sim, ResourceType t)
    {
        int n = 0;
        foreach (var node in sim.NodeList) if (node.Type == t) n++;
        return n;
    }

    // Work buildings hire from population now, so tests seed a workforce and give
    // it a beat to be taken on. Peasants spawn at the owner's drop-off.
    static void Seed(Simulation sim, int owner, int n)
    {
        for (int i = 0; i < n; i++) sim.SpawnPeasant(owner);
    }

    static void Settle(Simulation sim, int ticks = 8)
    {
        for (int i = 0; i < ticks; i++) sim.Tick(Array.Empty<Command>());
    }

    static int Peasants(Simulation sim, int owner)
    {
        int n = 0;
        foreach (var u in sim.Units) if (u.IsPeasant && u.Owner == owner && u.Alive) n++;
        return n;
    }

    static Unit Find(Simulation sim, int id)
    {
        foreach (var u in sim.Units) if (u.Id == id) return u;
        return null;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
