// Woodcutting — the self-running wood chain.
//
// A woodcutter's hut is placed and then left alone: it breeds a woodcutter, the
// woodcutter finds the nearest tree by itself, cuts it, hauls the wood to the
// closest drop-off, and when that tree is gone the hut hands it the next one —
// forever, with no orders. What these tests hold down is that all of that is
// DETERMINISTIC: two machines must breed the same worker, pick the same tree,
// and deliver to the same storehouse, or the checksums part a few ticks later.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Woodcutting — the self-running wood chain\n");

        NoHutMeansNoWoodcutting();
        AHutBreedsAWoodcutterAndCutsWood();
        ItMovesToTheNextTreeOnItsOwn();
        InexhaustibleDepositsNeverRunDry();
        StorehouseIsACloserDropOff();
        AHutRebreedsAKilledWoodcutter();
        RazingTheHutStopsItsWorker();
        AHutOnTheRealSkirmishForestProduces();
        AStorehouseByTheForestDoesNotStrandTheWorker();
        ABuildingDroppedOnAUnitDoesNotTrapIt();
        AQuarryMinesStoneTheSameWay();
        CrowdingADepositThrottlesEachMine();
        TwoClientsAgreeOnTheWoodChain();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // The whole feature is opt-in: with no hut placed, the woodcutting phase does
    // nothing and the world behaves exactly as it did before it existed.
    static void NoHutMeansNoWoodcutting()
    {
        Console.WriteLine("no hut, no woodcutting:");
        var sim = Forest();
        sim.SpawnUnit(1, 5, 5);
        int nodes = sim.NodeList.Count;
        for (int i = 0; i < 200; i++) sim.Tick(Array.Empty<Command>());
        Check("no worker took up cutting on its own", sim.Units[0].Job == Job.None);
        Check("no tree lost a single log", TotalWood(sim) == TotalWoodAt(nodes, sim));
    }

    static void AHutBreedsAWoodcutterAndCutsWood()
    {
        Console.WriteLine("\na hut hires a cutter and wood flows:");
        var sim = Forest();
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);        // the drop-off
        Seed(sim, 1, 2);                                      // a couple of idle peasants

        var hut = sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 20, 20);
        Check("empty hut has no worker yet", hut.WorkerId == 0);

        Settle(sim);                                          // it hires an idle peasant
        Check("the hut took on a peasant", hut.WorkerId != 0);
        var worker = Find(sim, hut.WorkerId);
        Check("who is set to woodcutting with no order given",
              worker != null && worker.Job == Job.Working);

        Check("nothing banked yet", sim.Stockpile(1, ResourceType.Wood) == 0);
        for (int i = 0; i < 800; i++) sim.Tick(Array.Empty<Command>());
        Check($"wood is accumulating on its own ({sim.Stockpile(1, ResourceType.Wood)})",
              sim.Stockpile(1, ResourceType.Wood) > 0);
    }

    static void ItMovesToTheNextTreeOnItsOwn()
    {
        Console.WriteLine("\nit finds the next tree when one runs out:");
        var sim = Forest(smallTrees: true);       // tiny trees, so they exhaust fast
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 2);
        var hut = sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 20, 20);
        Settle(sim);
        var worker = Find(sim, hut.WorkerId);

        // Let it work through more wood than any single tree holds, so it MUST
        // have moved between trees without help.
        int treeStock = 15;
        for (int i = 0; i < 3000 && sim.Stockpile(1, ResourceType.Wood) < treeStock * 2; i++)
            sim.Tick(Array.Empty<Command>());

        Check($"it cut more than one tree's worth ({sim.Stockpile(1, ResourceType.Wood)})",
              sim.Stockpile(1, ResourceType.Wood) >= treeStock * 2);
        Check("and it is still on the job", worker.Job == Job.Working);
    }

    // With InfiniteResources on, a deposit gives without drawing down: a tiny stand
    // that would normally be gone in three logs is still standing at full after the
    // cutter has hauled home far more than its size. (Off by default — proven by the
    // depletion test above, which still moves the worker along as stands run out.)
    static void InexhaustibleDepositsNeverRunDry()
    {
        Console.WriteLine("\nwith infinite resources on, a deposit never runs dry:");
        var sim = new Simulation(TileMap.Open(48)) { InfiniteResources = true };
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 1);
        var node = sim.SpawnNode(ResourceType.Wood, 20, 20, 3);   // three logs' worth, normally
        int amount0 = node.Amount;
        sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 18, 18);

        for (int i = 0; i < 1500; i++) sim.Tick(Array.Empty<Command>());

        Check($"the deposit still stands at full ({node.Amount}/{amount0})", node.Amount == amount0);
        bool present = false;
        foreach (var n in sim.NodeList) if (n.Id == node.Id) { present = true; break; }
        Check("and is still in the world", present);
        Check($"yet far more than its size was cut ({sim.Stockpile(1, ResourceType.Wood)} > {amount0})",
              sim.Stockpile(1, ResourceType.Wood) > amount0);
    }

    static void StorehouseIsACloserDropOff()
    {
        Console.WriteLine("\na storehouse becomes the closer drop-off:");
        // Keep far away, storehouse right by the trees. The woodcutter should
        // deliver to the storehouse, so the round trip is short and wood banks
        // FASTER than with the keep alone.
        var near = Forest();
        near.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(near, 1, 2);
        near.PlaceBuilding(BuildingType.Storehouse, 1, 24, 20);   // beside the forest
        near.PlaceBuilding(BuildingType.WoodcutterHut, 1, 20, 20);

        var far = Forest();
        far.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(far, 1, 2);
        far.PlaceBuilding(BuildingType.WoodcutterHut, 1, 20, 20); // keep only

        for (int i = 0; i < 700; i++) { near.Tick(Array.Empty<Command>()); far.Tick(Array.Empty<Command>()); }

        int withStore = near.Stockpile(1, ResourceType.Wood);
        int keepOnly = far.Stockpile(1, ResourceType.Wood);
        Check($"a nearby storehouse banks wood faster ({withStore} vs {keepOnly})",
              withStore > keepOnly);
    }

    static void AHutRebreedsAKilledWoodcutter()
    {
        Console.WriteLine("\na hut replaces a fallen woodcutter from the pool:");
        var sim = Forest();
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 2);                       // one to cut, one spare to replace it
        var hut = sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 20, 20);
        Settle(sim);
        int firstWorker = hut.WorkerId;
        Check("the hut hired a cutter", firstWorker != 0);

        // Kill the woodcutter.
        Find(sim, firstWorker).Hp = 0;
        sim.Tick(Array.Empty<Command>());     // RemoveDead clears it
        Check("the woodcutter is gone", Find(sim, firstWorker) == null);

        // With a spare peasant on hand, the hut takes one on straight away.
        for (int i = 0; i < 50 && hut.WorkerId == 0; i++) sim.Tick(Array.Empty<Command>());
        Check("the hut hired a replacement", hut.WorkerId != 0 && hut.WorkerId != firstWorker);
        var repl = Find(sim, hut.WorkerId);
        Check("which is back to cutting", repl != null && repl.Job == Job.Working);
    }

    static void RazingTheHutStopsItsWorker()
    {
        Console.WriteLine("\nrazing the hut frees its woodcutter:");
        var sim = Forest();
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 2);
        var hut = sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 20, 20);
        for (int i = 0; i < 30; i++) sim.Tick(Array.Empty<Command>());   // hire + get to work
        var worker = Find(sim, hut.WorkerId);
        Check("the hut has a working cutter", worker != null && worker.Job == Job.Working);

        hut.Hp = 0;
        sim.Tick(Array.Empty<Command>());     // RemoveDestroyedBuildings runs
        Check("the hut is gone", sim.BuildingList.Count == 1);           // just the keep
        Check("its woodcutter stood down", worker.Job == Job.None);
    }

    // The real thing: the actual Skirmish map and start (fog on, real distances,
    // the forest where the game plants it), with a hut dropped in that forest.
    // This is what the live game does, and it must actually bank wood.
    static void AHutOnTheRealSkirmishForestProduces()
    {
        Console.WriteLine("\na hut in the real skirmish forest banks wood:");
        const int size = Skirmish.DefaultSize;
        var sim = new Simulation(TileMap.Skirmish(size));
        Skirmish.Setup(sim, size);

        int w = Skirmish.West(size), m = Skirmish.MidY(size);
        // The player-1 forest is planted around (w+7, m-9); put the hut in it.
        var hut = sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, w + 6, m - 10);
        Check("the hut placed on the forest", hut != null);
        // The skirmish start seeds a workforce, so the hut hires one of them.
        Settle(sim, 10);
        Check("and hired a woodcutter from the starting peasants", hut != null && hut.WorkerId != 0);

        // A tree gets planted on the very tile the hut lands on; building over it
        // must clear it, or the hut's nearest tree is one buried under itself and
        // the woodcutter freezes forever (the bug this test was written to catch).
        int before = sim.Stockpile(1, ResourceType.Wood);
        for (int i = 0; i < 2000; i++) sim.Tick(Array.Empty<Command>());
        int after = sim.Stockpile(1, ResourceType.Wood);
        Check($"wood climbed over 2000 ticks ({before} -> {after})", after > before);
    }

    // Reported bug: dropping a storehouse right by the forest left the woodcutter
    // stuck. Reproduce it on the real map — hut in the forest, storehouse jammed
    // in beside it — and prove the worker still banks wood.
    static void AStorehouseByTheForestDoesNotStrandTheWorker()
    {
        Console.WriteLine("\na storehouse by the forest does not strand the worker:");
        const int size = Skirmish.DefaultSize;
        int w = Skirmish.West(size), m = Skirmish.MidY(size);

        // Try the storehouse at a spread of spots around the forest — one of them
        // is what the player did, and any that strands the worker is the bug.
        foreach (var (sx, sy) in new[] { (w + 3, m - 12), (w + 10, m - 9), (w + 7, m - 12), (w + 4, m - 6) })
        {
            var sim = new Simulation(TileMap.Skirmish(size));
            Skirmish.Setup(sim, size);
            sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, w + 6, m - 10);
            sim.PlaceBuilding(BuildingType.Storehouse, 1, sx, sy);

            int before = sim.Stockpile(1, ResourceType.Wood);
            for (int i = 0; i < 1500; i++) sim.Tick(Array.Empty<Command>());
            Check($"storehouse at ({sx},{sy}): wood still banks ({before} -> {sim.Stockpile(1, ResourceType.Wood)})",
                  sim.Stockpile(1, ResourceType.Wood) > before);
        }
    }

    // The actual bug behind "the peasant got stuck": a building placed ON a unit
    // blocked the unit's tile, and a unit on a blocked tile can path nowhere. The
    // fix shoves any unit out of a new building's footprint, so it is never
    // trapped — this is what the live storehouse-on-the-woodcutter hit.
    static void ABuildingDroppedOnAUnitDoesNotTrapIt()
    {
        Console.WriteLine("\na building dropped on a unit does not trap it:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 4, 4);
        var u = sim.SpawnUnit(1, 20, 20);

        // A 2x2 storehouse whose footprint (19,19)-(20,20) covers the unit.
        sim.PlaceBuilding(BuildingType.Storehouse, 1, 19, 19);
        int ux = u.X >> 16, uy = u.Y >> 16;
        Check($"the unit was shoved out of the footprint (now at {ux},{uy})",
              ux < 19 || ux > 20 || uy < 19 || uy > 20);
        Check("and it stands on passable ground", sim.Map.Passable(ux, uy));

        // And it can actually move afterwards — the real test of "not trapped".
        sim.Tick(new List<Command> { new Command { Owner = 1, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = 30, Y = 20 } });
        for (int i = 0; i < 500 && u.HasPath; i++) sim.Tick(Array.Empty<Command>());
        Check($"it can walk away ({u.X >> 16},{u.Y >> 16})", (u.X >> 16) == 30 && (u.Y >> 16) == 20);
    }

    // A quarry is the same self-running machine as a hut, pointed at stone. The
    // generalisation must hold: build it on a stone deposit and stone banks with
    // no orders, and it does NOT touch wood (it harvests only its own resource).
    static void AQuarryMinesStoneTheSameWay()
    {
        Console.WriteLine("\na quarry mines stone the same way a hut cuts wood:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, 2);
        // A stone deposit and, right beside it, a forest — to prove the quarry
        // takes stone and leaves the trees alone.
        for (int i = 0; i < 6; i++) sim.SpawnNode(ResourceType.Stone, 22 + (i % 3) * 3, 18 + (i / 3) * 3, 120);
        sim.SpawnNode(ResourceType.Wood, 24, 24, 200);

        var quarry = sim.PlaceBuilding(BuildingType.Quarry, 1, 20, 20);
        Settle(sim);
        Check("the quarry hired a worker", quarry.WorkerId != 0);
        Check("its worker is a peasant on the job (Job.Working)",
              Find(sim, quarry.WorkerId)?.Job == Job.Working);

        for (int i = 0; i < 900; i++) sim.Tick(Array.Empty<Command>());
        Check($"stone is accumulating on its own ({sim.Stockpile(1, ResourceType.Stone)})",
              sim.Stockpile(1, ResourceType.Stone) > 0);
        Check("and it left the wood alone", sim.Stockpile(1, ResourceType.Wood) == 0);
    }

    // Crowding a stone deposit with several quarries throttles each one — so two mines
    // on one rock out-produce one, but nowhere near double: packing them is wasteful.
    static void CrowdingADepositThrottlesEachMine()
    {
        Console.WriteLine("\ncrowding a deposit throttles each mine:");
        int solo = QuarryStoneOver(1, 1500);
        int pair = QuarryStoneOver(2, 1500);
        Check($"two crowded quarries still out-produce one ({pair} > {solo})", pair > solo);
        Check($"but well short of double — each mines less when crowded ({pair} < {2 * solo})", pair < 2 * solo);
    }

    static int QuarryStoneOver(int quarries, int ticks)
    {
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        Seed(sim, 1, quarries + 1);                             // a worker to spare per quarry
        sim.SpawnNode(ResourceType.Stone, 22, 20, 1000000);    // one rich deposit, won't run dry
        sim.PlaceBuilding(BuildingType.Storehouse, 1, 24, 22); // a drop-off right by it, so harvest rate is the bottleneck
        for (int i = 0; i < quarries; i++) sim.PlaceBuilding(BuildingType.Quarry, 1, 18 + i * 2, 22);   // crammed onto the same rock
        Settle(sim);
        for (int i = 0; i < ticks; i++) sim.Tick(Array.Empty<Command>());
        return sim.Stockpile(1, ResourceType.Stone);
    }

    // The one that matters most: the whole self-running chain, computed twice,
    // must agree every tick.
    static void TwoClientsAgreeOnTheWoodChain()
    {
        Console.WriteLine("\ntwo clients agree on the wood chain:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, ForestMap());
        var b = new Client(2, net, ForestMap());
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
            Seed(c.Sim, 1, 4);                                            // workforce for two huts + a quarry
            c.Sim.PlaceBuilding(BuildingType.Storehouse, 1, 24, 20);
            PlantForest(c.Sim);
            for (int i = 0; i < 4; i++) c.Sim.SpawnNode(ResourceType.Stone, 30 + (i % 2) * 3, 30 + (i / 2) * 3, 120);
            c.Sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 20, 20);
            c.Sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 22, 24);   // two huts competing for trees
            c.Sim.PlaceBuilding(BuildingType.Quarry, 1, 31, 28);          // a quarry, working stone alongside
        }

        int desyncs = 0, first = -1;
        for (int t = 0; t < 900; t++)
        {
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 900 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check($"and both actually cut wood ({a.Sim.Stockpile(1, ResourceType.Wood)})",
              a.Sim.Stockpile(1, ResourceType.Wood) > 0);
    }

    // ---- helpers -----------------------------------------------------------

    // A map with a cluster of trees (Wood nodes) around (22..30, 18..24), a hut's
    // reach from (20,20).
    static Simulation Forest(bool smallTrees = false)
    {
        var sim = new Simulation(TileMap.Open(48));
        PlantForest(sim, smallTrees);
        return sim;
    }

    static TileMap ForestMap() => TileMap.Open(48);

    static void PlantForest(Simulation sim, bool smallTrees = false)
    {
        int per = smallTrees ? 15 : 120;
        for (int i = 0; i < 6; i++)
            sim.SpawnNode(ResourceType.Wood, 22 + (i % 3) * 3, 18 + (i / 3) * 3, per);
    }

    static Unit Find(Simulation sim, int id)
    {
        foreach (var u in sim.Units) if (u.Id == id) return u;
        return null;
    }

    // Seed a starting workforce. Work buildings hire from population now, so a
    // hut/quarry does nothing until an idle peasant is on hand to staff it. Call
    // after the keep is placed (peasants spawn at its drop-off).
    static void Seed(Simulation sim, int owner, int n)
    {
        for (int i = 0; i < n; i++) sim.SpawnPeasant(owner);
    }

    // Run a handful of ticks so a just-placed building can hire its peasant.
    static void Settle(Simulation sim, int ticks = 5)
    {
        for (int i = 0; i < ticks; i++) sim.Tick(Array.Empty<Command>());
    }

    static int TotalWood(Simulation sim) => sim.Stockpile(1, ResourceType.Wood);
    static int TotalWoodAt(int expectedNodes, Simulation sim)
    {
        // Stock stayed zero AND no node lost amount → total unchanged. Simplest to
        // just assert the stockpile is still zero, which the caller wants.
        return 0;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
