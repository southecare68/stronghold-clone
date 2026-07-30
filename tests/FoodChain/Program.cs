// FoodChain — a farm reaps its field straight into food.
//
// The farm is a work building like the woodcutter's hut, pointed at a CROP FIELD it
// plants for itself; its farmer reaps FOOD and hauls it home — the way a Stronghold
// apple orchard or dairy feeds a castle, no mill-and-bakery chain to staff. Food is
// the goal resource: it feeds the realm's rations and breeds population.
//
// As with the wood chain, the failures that bite are determinism ones — a farm that
// sows its field on a different tile desyncs two machines a moment later — so the
// two-client check is the point. The rest pins down that food flows off the field
// and that a razed farm frees its farmer.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("FoodChain — farm → food\n");

        NoFarmMeansNoFood();
        AFarmPlantsAFieldAndFoodFlows();
        AFarmReplantsItsFieldSoItNeverRunsDry();
        AnInexhaustibleFieldStaysPutAndNeverEmpties();
        EveryPassableTileGrowsAField();
        RicherSoilYieldsMoreFood();
        AFarmFeedsAndGrowsTheRealm();
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

    // Opt-in: with no farm, no crop field is sown and no food is reaped.
    static void NoFarmMeansNoFood()
    {
        Console.WriteLine("no farm, no food from farming:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 6, 6);
        Seed(sim, 1, 2);
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check("no food appeared from nowhere", sim.Stockpile(1, ResourceType.Food) == 0);
        Check("and no field was sown", NodesOfType(sim, ResourceType.Food) == 0);
    }

    // Placing a farm alone breeds a farmer, sows a field, and food begins banking
    // with no orders given — the whole self-running cycle, on a bare grass map. Uses
    // a plain drop-off, no keep, so no rations eat the harvest as it lands.
    static void AFarmPlantsAFieldAndFoodFlows()
    {
        Console.WriteLine("\na farm sows a field and food flows:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 4, 4);
        Seed(sim, 1, 2);

        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        Check("a crop field was sown at once", NodesOfType(sim, ResourceType.Food) >= 1);
        Check("empty farm has no worker yet", farm.WorkerId == 0);

        Settle(sim);
        Check("the farm took on a farmer", farm.WorkerId != 0);
        Check("its worker is a peasant on the job (Job.Working)",
              Find(sim, farm.WorkerId)?.Job == Job.Working);

        for (int i = 0; i < 900; i++) sim.Tick(Array.Empty<Command>());
        Check($"food is accumulating on its own ({sim.Stockpile(1, ResourceType.Food)})",
              sim.Stockpile(1, ResourceType.Food) > 0);
    }

    // A farm is renewable: reap far more food than a single field holds, and it keeps
    // coming — the farm sows a fresh field each time the last is cut down.
    static void AFarmReplantsItsFieldSoItNeverRunsDry()
    {
        Console.WriteLine("\na farm replants its field and never runs dry:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 4, 4);
        Seed(sim, 1, 2);
        var store = sim.PlaceBuilding(BuildingType.Granary, 1, 15, 16);   // clear of the 3x3 farm
        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        Check("farm and granary both fit", store != null && farm != null);

        for (int i = 0; i < 6000; i++) sim.Tick(Array.Empty<Command>());
        int banked = sim.Stockpile(1, ResourceType.Food);
        Check($"reaped more than a single field's worth ({banked} > 240)", banked > 240);
        Check("and a field is still standing to be cut",
              NodesOfType(sim, ResourceType.Food) >= 1);
    }

    // With InfiniteResources on, a farm reaps its ONE field IN PLACE — it never draws
    // down and never replants onto fresh ground, so the field stays a single tile and
    // farms can pack tight. (Off, the farm replants across tiles — proven above.)
    static void AnInexhaustibleFieldStaysPutAndNeverEmpties()
    {
        Console.WriteLine("\nwith infinite resources on, a field stays put and never empties:");
        var sim = new Simulation(TileMap.Open(48)) { InfiniteResources = true };
        sim.SetDropOff(1, 4, 4);
        Seed(sim, 1, 1);
        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        Check("the farm fits", farm != null);

        Settle(sim, 80);                       // hire the farmer, sow the field
        var field = FoodField(sim);
        Check("a crop field was sown", field != null);
        int full = field?.Amount ?? 0;

        for (int i = 0; i < 3000; i++) sim.Tick(Array.Empty<Command>());
        Check($"the field is still full, never drawn down ({field?.Amount}/{full})",
              field != null && field.Amount == full);
        Check($"and it is still the only field, no replant onto fresh ground ({NodesOfType(sim, ResourceType.Food)})",
              NodesOfType(sim, ResourceType.Food) == 1);
        Check($"yet food kept flowing off that full field ({sim.Stockpile(1, ResourceType.Food)} banked)",
              sim.Stockpile(1, ResourceType.Food) > 0);
    }

    // Every passable tile grows at least a one-food field, so a farm on plain,
    // un-improved ground still feeds you. Only a deposit tile (or water/rock) grows
    // nothing: a field can't overlap a forest, quarry or mine.
    static void EveryPassableTileGrowsAField()
    {
        Console.WriteLine("\nany passable tile grows a field; a deposit tile does not:");

        var plain = new Simulation(TileMap.Open(48)) { RequireFertileSoil = true };
        plain.SetDropOff(1, 14, 14);
        Seed(plain, 1, 1);
        plain.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        for (int i = 0; i < 1500; i++) plain.Tick(Array.Empty<Command>());
        Check($"a farm on plain ground still banks food ({plain.Stockpile(1, ResourceType.Food)})",
              plain.Stockpile(1, ResourceType.Food) > 0);

        var withOre = new Simulation(TileMap.Open(48));
        Check($"plain ground has food value 1 ({withOre.FoodYieldAt(20, 20)})", withOre.FoodYieldAt(20, 20) == 1);
        withOre.SpawnNode(ResourceType.Iron, 20, 20, 100);
        Check($"an ore tile drops to 0 food ({withOre.FoodYieldAt(20, 20)})", withOre.FoodYieldAt(20, 20) == 0);
        Check($"but plain ground beside it still grows a field ({withOre.FoodYieldAt(21, 20)})", withOre.FoodYieldAt(21, 20) == 1);
    }

    // Soil comes in grades, and a field reaps more per gather on richer ground — so
    // the SAME farm, run the same time, banks far more food on prime soil than thin.
    static void RicherSoilYieldsMoreFood()
    {
        Console.WriteLine("\nricher soil yields more food:");
        int Bank(Terrain grade)
        {
            var map = TileMap.Open(48);
            for (int y = 18; y <= 26; y++) for (int x = 18; x <= 26; x++) map.Set(x, y, grade);
            var sim = new Simulation(map) { RequireFertileSoil = true };
            sim.SetDropOff(1, 14, 14);
            Seed(sim, 1, 1);
            sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
            for (int i = 0; i < 1500; i++) sim.Tick(Array.Empty<Command>());
            return sim.Stockpile(1, ResourceType.Food);
        }
        int poor = Bank(Terrain.FertilePoor), rich = Bank(Terrain.FertileRich);
        Check($"thin soil still yields something ({poor})", poor > 0);
        Check($"and prime soil out-yields it ({rich} > {poor})", rich > poor);
    }

    // End to end on an empty larder: a farm, staffed by a starting workforce, must
    // reap food and — the whole point — turn it into new peasants, all by itself.
    static void AFarmFeedsAndGrowsTheRealm()
    {
        Console.WriteLine("\na farm feeds the realm and grows it:");
        var sim = new Simulation(TileMap.Open(64));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 2);                                                     // a farmer, plus a spare
        int seeded = Peasants(sim, 1);
        var farm = sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
        Check("the farm fits", farm != null);

        Check("larder starts empty", sim.Stockpile(1, ResourceType.Food) == 0);
        for (int i = 0; i < 5000; i++) sim.Tick(Array.Empty<Command>());
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

    // A granary is a closer drop-off for the harvest: drop one beside a farm far from
    // the keep and its food banks FASTER, the round trip cut short.
    static void AGranaryIsACloserDropOffForTheHarvest()
    {
        Console.WriteLine("\na granary is the closer drop-off for the harvest:");
        var near = new Simulation(TileMap.Open(64));
        near.SetDropOff(1, 2, 2);
        Seed(near, 1, 1);
        var g = near.PlaceBuilding(BuildingType.Granary, 1, 34, 30);   // beside the field
        near.PlaceBuilding(BuildingType.Farm, 1, 30, 30);
        Check("the granary fits by the field", g != null);

        var far = new Simulation(TileMap.Open(64));
        far.SetDropOff(1, 2, 2);
        Seed(far, 1, 1);
        far.PlaceBuilding(BuildingType.Farm, 1, 30, 30);              // drop-off only, a long haul home

        for (int i = 0; i < 900; i++) { near.Tick(Array.Empty<Command>()); far.Tick(Array.Empty<Command>()); }

        int withGranary = near.Stockpile(1, ResourceType.Food);
        int keepOnly = far.Stockpile(1, ResourceType.Food);
        Check($"a nearby granary banks food faster ({withGranary} vs {keepOnly})",
              withGranary > keepOnly);
    }

    // Population cannot outgrow its housing: a well-fed, fairly-taxed realm draws
    // newcomers until every bed is full, then stops; a house lifts the cap by ten.
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

        sim.PlaceBuilding(BuildingType.House, 1, 20, 20);
        int withHouse = sim.PopulationCap(1);
        Check($"a house raised the cap by ten ({keepCap} -> {withHouse})", withHouse == keepCap + 10);

        for (int i = 0; i < 2000; i++) sim.Tick(Array.Empty<Command>());
        Check($"population grew to the new cap ({sim.PeasantCount(1)}/{withHouse})",
              sim.PeasantCount(1) == withHouse);

        int foodAtCap = sim.Stockpile(1, ResourceType.Food);
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());
        Check($"rations still draw the larder at the cap ({foodAtCap} -> {sim.Stockpile(1, ResourceType.Food)})",
              sim.Stockpile(1, ResourceType.Food) < foodAtCap);
    }

    // A standing army eats: each soldier draws food every meal, the draw is exactly
    // the army size per interval, and an empty larder floors at zero.
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
        Check($"it ate its keep ({eaten} of 200 over 600 ticks)", eaten == 100);

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

    // Upkeep is pure integer bookkeeping, so two machines must draw the larder down
    // in lockstep.
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

    // The one that matters most: the whole food economy, computed twice, must agree
    // on every tick — the field tiles and the haul home.
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
            Seed(c.Sim, 1, 4);                                           // farmers for two farms, plus growth
            c.Sim.AddResource(1, ResourceType.Food, 120);                // an opening larder so the ramp doesn't starve
            c.Sim.PlaceBuilding(BuildingType.Granary, 1, 15, 16);
            var f1 = c.Sim.PlaceBuilding(BuildingType.Farm, 1, 20, 20);
            var f2 = c.Sim.PlaceBuilding(BuildingType.Farm, 1, 26, 26);   // two farms, well clear of each other
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
        Check($"and the workforce grew from the harvest ({Peasants(a.Sim, 1)} on A)",
              Peasants(a.Sim, 1) > 4);
    }

    // ---- helpers -----------------------------------------------------------

    static int NodesOfType(Simulation sim, ResourceType t)
    {
        int n = 0;
        foreach (var node in sim.NodeList) if (node.Type == t) n++;
        return n;
    }

    static ResourceNode FoodField(Simulation sim)
    {
        foreach (var n in sim.NodeList) if (n.Type == ResourceType.Food) return n;
        return null;
    }

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
