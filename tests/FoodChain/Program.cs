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
        AnInexhaustibleFieldStaysPutAndNeverEmpties();
        FieldsGrowOnlyOnFertileSoil();
        RicherSoilYieldsMoreGrain();
        AMillGrindsGrainIntoFlour();
        AMillWithNoGrainStaysIdle();
        ABakeryBakesFlourIntoBread();
        TheWholeChainTurnsAnEmptyLarderIntoFood();
        RazingTheFarmStopsItsFarmer();
        AGranaryIsACloserDropOffForTheHarvest();
        PopulationIsCappedByHousing();
        AStandingArmyEatsFood();
        NoArmyMeansNoUpkeep();
        TwoClientsAgreeOnUpkeep();
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

    // With InfiniteResources on, a farm reaps its ONE field IN PLACE — it never
    // draws down and never replants onto fresh ground, so the field stays a single
    // tile and farms can pack tight, freeing land for other buildings. (Off, the
    // farm replants across tiles as fields run out — proven directly above.)
    static void AnInexhaustibleFieldStaysPutAndNeverEmpties()
    {
        Console.WriteLine("\nwith infinite resources on, a field stays put and never empties:");
        var sim = new Simulation(TileMap.Open(48)) { InfiniteResources = true };
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 1);
        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        Check("the farm fits", farm != null);

        Settle(sim, 80);                       // hire the farmer, sow the field
        var field = GrainField(sim);
        Check("a wheat field was sown", field != null);
        int full = field?.Amount ?? 0;

        for (int i = 0; i < 3000; i++) sim.Tick(Array.Empty<Command>());
        Check($"the field is still full, never drawn down ({field?.Amount}/{full})",
              field != null && field.Amount == full);
        Check($"and it is still the only field, no replant onto fresh ground ({NodesOfType(sim, ResourceType.Grain)})",
              NodesOfType(sim, ResourceType.Grain) == 1);
        // Grain banked while the field held at full: a finite field would have been
        // drawn down by exactly this haul, so the two together prove it gave freely.
        Check($"yet grain kept flowing off that full field ({sim.Stockpile(1, ResourceType.Grain)} banked)",
              sim.Stockpile(1, ResourceType.Grain) > 0);
    }

    // With RequireFertileSoil on, a farm's field grows ONLY where the ground is
    // fertile: a farm ringed by fertile soil sows and reaps; one on barren ground
    // sows nothing at all. This is what makes WHERE you build a farm matter.
    static void FieldsGrowOnlyOnFertileSoil()
    {
        Console.WriteLine("\nwith fertile soil required, only a farm on it yields:");

        var goodMap = TileMap.Open(48);
        for (int y = 18; y <= 26; y++) for (int x = 18; x <= 26; x++) goodMap.Set(x, y, Terrain.Fertile);
        var good = new Simulation(goodMap) { RequireFertileSoil = true };
        good.PlaceBuilding(BuildingType.Keep, 1, 14, 14);
        Seed(good, 1, 1);
        good.PlaceBuilding(BuildingType.Farm, 1, 20, 20);          // ringed by fertile soil
        for (int i = 0; i < 1500; i++) good.Tick(Array.Empty<Command>());
        Check($"a farm on fertile soil sows and banks grain ({good.Stockpile(1, ResourceType.Grain)})",
              good.Stockpile(1, ResourceType.Grain) > 0);

        var barren = new Simulation(TileMap.Open(48)) { RequireFertileSoil = true };
        barren.PlaceBuilding(BuildingType.Keep, 1, 14, 14);
        Seed(barren, 1, 1);
        barren.PlaceBuilding(BuildingType.Farm, 1, 20, 20);        // no fertile ground anywhere
        for (int i = 0; i < 1500; i++) barren.Tick(Array.Empty<Command>());
        Check($"a farm on barren ground sows no field ({NodesOfType(barren, ResourceType.Grain)})",
              NodesOfType(barren, ResourceType.Grain) == 0);
        Check($"and banks no grain ({barren.Stockpile(1, ResourceType.Grain)})",
              barren.Stockpile(1, ResourceType.Grain) == 0);
    }

    // Soil comes in grades, and a field reaps more per gather on richer ground — so
    // the SAME farm, run the same time, banks far more grain on prime soil than thin.
    static void RicherSoilYieldsMoreGrain()
    {
        Console.WriteLine("\nricher soil yields more grain:");
        int Bank(Terrain grade)
        {
            var map = TileMap.Open(48);
            for (int y = 18; y <= 26; y++) for (int x = 18; x <= 26; x++) map.Set(x, y, grade);
            var sim = new Simulation(map) { RequireFertileSoil = true };
            sim.PlaceBuilding(BuildingType.Keep, 1, 14, 14);
            Seed(sim, 1, 1);
            sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
            for (int i = 0; i < 1500; i++) sim.Tick(Array.Empty<Command>());
            return sim.Stockpile(1, ResourceType.Grain);
        }
        int poor = Bank(Terrain.FertilePoor), rich = Bank(Terrain.FertileRich);
        Check($"thin soil still yields something ({poor})", poor > 0);
        Check($"and prime soil out-yields it ({rich} > {poor})", rich > poor);
    }

    static ResourceNode GrainField(Simulation sim)
    {
        foreach (var n in sim.NodeList) if (n.Type == ResourceType.Grain) return n;
        return null;
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

    // A granary is the storehouse's twin for the food chain: drop one beside a farm
    // far from the keep and its grain banks FASTER, the round trip cut short — the
    // same closer-drop-off logic the storehouse gives the woodcutter.
    static void AGranaryIsACloserDropOffForTheHarvest()
    {
        Console.WriteLine("\na granary is the closer drop-off for the harvest:");
        var near = new Simulation(TileMap.Open(64));
        near.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(near, 1, 1);
        var g = near.PlaceBuilding(BuildingType.Granary, 1, 34, 30);   // beside the field
        near.PlaceBuilding(BuildingType.Farm, 1, 30, 30);
        Check("the granary fits by the field", g != null);

        var far = new Simulation(TileMap.Open(64));
        far.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(far, 1, 1);
        far.PlaceBuilding(BuildingType.Farm, 1, 30, 30);              // keep only, a long haul home

        for (int i = 0; i < 900; i++) { near.Tick(Array.Empty<Command>()); far.Tick(Array.Empty<Command>()); }

        int withGranary = near.Stockpile(1, ResourceType.Grain);
        int keepOnly = far.Stockpile(1, ResourceType.Grain);
        Check($"a nearby granary banks grain faster ({withGranary} vs {keepOnly})",
              withGranary > keepOnly);
    }

    // Population cannot outgrow its housing: a well-fed, fairly-taxed realm draws
    // newcomers until every bed is full, then stops; a house lifts the cap by ten
    // and the immigrants keep coming to fill it. Rations feed that popularity, so
    // the larder is kept brimming here — this test isolates HOUSING as the limit,
    // not food. (That rations draw food continuously is proven separately below.)
    static void PopulationIsCappedByHousing()
    {
        Console.WriteLine("\npopulation is capped by housing:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        sim.AddResource(1, ResourceType.Food, 100000);   // larder never runs dry, so ONLY housing limits growth
        Seed(sim, 1, 1);

        int keepCap = sim.PopulationCap(1);
        Check($"the keep alone houses a starting court ({keepCap})", keepCap > 0);

        for (int i = 0; i < 2000; i++) sim.Tick(Array.Empty<Command>());
        Check($"population grew to the keep's cap and stopped ({sim.PeasantCount(1)}/{keepCap})",
              sim.PeasantCount(1) == keepCap);

        // A house shelters ten more.
        sim.PlaceBuilding(BuildingType.House, 1, 20, 20);
        int withHouse = sim.PopulationCap(1);
        Check($"a house raised the cap by ten ({keepCap} -> {withHouse})", withHouse == keepCap + 10);

        for (int i = 0; i < 2000; i++) sim.Tick(Array.Empty<Command>());
        Check($"population grew to the new cap ({sim.PeasantCount(1)}/{withHouse})",
              sim.PeasantCount(1) == withHouse);

        // Rations are a standing cost: even at the cap, a fed populace keeps eating,
        // so the larder is still being drawn down (the opposite of the old model,
        // where food was spent only at birth and idled once the court was full).
        int foodAtCap = sim.Stockpile(1, ResourceType.Food);
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check($"rations still draw the larder at the cap ({foodAtCap} -> {sim.Stockpile(1, ResourceType.Food)})",
              sim.Stockpile(1, ResourceType.Food) < foodAtCap);
    }

    // A standing army eats: each soldier draws food from the larder every meal,
    // the draw is exactly the army size per interval, and an empty larder floors
    // at zero rather than going negative.
    static void AStandingArmyEatsFood()
    {
        Console.WriteLine("\na standing army eats food as upkeep:");
        var sim = new Simulation(TileMap.Open(48));
        sim.AddResource(1, ResourceType.Food, 200);
        for (int i = 0; i < 10; i++) sim.SpawnUnit(1, 5 + i, 5);   // ten soldiers (non-peasant)
        Check("the army is ten strong", sim.ArmySize(1) == 10);

        int start = sim.Stockpile(1, ResourceType.Food);
        for (int i = 0; i < 600; i++) sim.Tick(Array.Empty<Command>());
        int eaten = start - sim.Stockpile(1, ResourceType.Food);
        // 10 soldiers x 1 food, ten meals over 600 ticks (one every 60) = 100.
        Check($"it ate its keep ({eaten} of 200 over 600 ticks)", eaten == 100);

        // Run the larder dry: it floors at zero, never negative.
        for (int i = 0; i < 3000; i++) sim.Tick(Array.Empty<Command>());
        Check($"a drained larder floors at zero ({sim.Stockpile(1, ResourceType.Food)})",
              sim.Stockpile(1, ResourceType.Food) == 0);
    }

    // Peasants are not charged upkeep — their food cost was paid at birth. With no
    // army, a larder is not touched by upkeep at all.
    static void NoArmyMeansNoUpkeep()
    {
        Console.WriteLine("\nno army, no upkeep:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 5, 5);                 // spawn point, but NO keep => no breeding either
        sim.AddResource(1, ResourceType.Food, 100);
        for (int i = 0; i < 5; i++) sim.SpawnPeasant(1);
        for (int i = 0; i < 600; i++) sim.Tick(Array.Empty<Command>());
        Check($"the larder is untouched with no soldiers ({sim.Stockpile(1, ResourceType.Food)})",
              sim.Stockpile(1, ResourceType.Food) == 100);
    }

    // Upkeep is pure integer bookkeeping, so two machines must draw the larder
    // down in lockstep.
    static void TwoClientsAgreeOnUpkeep()
    {
        Console.WriteLine("\ntwo clients agree on army upkeep:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(48));
        var b = new Client(2, net, TileMap.Open(48));
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.AddResource(1, ResourceType.Food, 300);
            for (int i = 0; i < 8; i++) c.Sim.SpawnUnit(1, 5 + i, 5);
        }

        int desyncs = 0;
        for (int t = 0; t < 800; t++)
        {
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) desyncs++;
        }
        Check($"StateChecksum identical over 800 ticks of upkeep ({desyncs} desyncs)", desyncs == 0);
        Check($"and the larder was drawn down ({a.Sim.Stockpile(1, ResourceType.Food)} of 300)",
              a.Sim.Stockpile(1, ResourceType.Food) < 300);
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
            c.Sim.AddResource(1, ResourceType.Food, 120);                // an opening larder, like a real start, so the ramp doesn't starve
            c.Sim.PlaceBuilding(BuildingType.Storehouse, 1, 15, 16);
            var f1 = c.Sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
            var f2 = c.Sim.PlaceBuilding(BuildingType.Farm, 1, 26, 26);   // two farms, well clear of each other
            c.Sim.PlaceBuilding(BuildingType.Mill, 1, 40, 30);
            c.Sim.PlaceBuilding(BuildingType.Bakery, 1, 44, 30);
            if (f1 == null || f2 == null) Check("both farms fit", false);
        }

        int desyncs = 0, first = -1;
        for (int t = 0; t < 2200; t++)
        {
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 2200 ticks" +
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
