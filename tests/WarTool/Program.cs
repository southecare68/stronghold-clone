// WarTool — the branches' ⚔ war-tool payoffs ("war feeds the attacker").
//
// A researched war-tool turns each enemy your soldiers cut down into fuel for that
// path: Privateers pillage gold into your hoard, War Loot strips wood & stone to
// fund wonders, a Crusade emboldens the faith. (Domain's war-tool is Conquest,
// which takes a whole keep — covered by tests/Conquest.)
//
// Each test runs the SAME one-sided battle twice — once with the node researched,
// once without — over a fixed seed, so the fight (and its kills) is identical. The
// only difference is the payoff, so the node alone accounts for the surplus.
//
// Sim-only, like the other economy suites. Run with `dotnet run`.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;
    static readonly Command[] None = Array.Empty<Command>();

    static readonly int[] EconChain = { TechTree.Roads, TechTree.Market, TechTree.TradePost, TechTree.Monopoly, TechTree.BankingHouse };
    static readonly int[] SciChain  = { TechTree.Roads, TechTree.ScholarsHut, TechTree.Library, TechTree.Engineering, TechTree.PrintingPress };
    static readonly int[] RelChain  = { TechTree.Roads, TechTree.Chapel, TechTree.Shrine, TechTree.Missionaries, TechTree.Cathedral };

    static void Main()
    {
        Console.WriteLine("WarTool — war feeds the attacker\n");

        // Privateers → gold.
        var pOn = Battle(EconChain, TechTree.Privateers);
        var pOff = Battle(EconChain, -1);
        Check($"Privateers pillage gold from kills (+{pOn.gold - pOff.gold})", pOn.gold > pOff.gold);

        // War Loot → wood & stone.
        var wOn = Battle(SciChain, TechTree.WarLoot);
        var wOff = Battle(SciChain, -1);
        Check($"War Loot strips materials from kills (+{wOn.wood - wOff.wood}w/{wOn.stone - wOff.stone}s)",
              wOn.wood > wOff.wood && wOn.stone > wOff.stone);

        // Crusade → faith.
        var cOn = Battle(RelChain, TechTree.Crusade);
        var cOff = Battle(RelChain, -1);
        Check($"a Crusade emboldens the faith on kills (+{cOn.faith - cOff.faith})", cOn.faith > cOff.faith);

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // One one-sided battle: owner 1 (with the branch, and optionally the war-tool)
    // cuts down five passive enemies, and we read owner 1's stock afterward.
    static (int gold, int wood, int stone, int faith) Battle(int[] chain, int warTool)
    {
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 4, 4);
        sim.PlaceBuilding(BuildingType.Keep, 2, 40, 40);
        sim.AddResearch(1, 3000);
        foreach (int id in chain) sim.TryResearch(1, id);
        if (warTool >= 0) sim.TryResearch(1, warTool);

        // A killing party and five sitting ducks, clustered within aggro range.
        var army = new List<int>();
        for (int i = 0; i < 10; i++) army.Add(sim.SpawnUnit(1, 16 + i % 3, 16 + i / 3).Id);
        int firstFoe = 0;
        for (int i = 0; i < 5; i++) { int id = sim.SpawnUnit(2, 20 + i % 3, 20 + i / 3).Id; if (i == 0) firstFoe = id; }

        sim.Tick(new[] { new Command { Owner = 1, Type = CommandType.Attack, UnitIds = army.ToArray(), TargetId = firstFoe } });
        for (int t = 0; t < 2000 && sim.ArmySize(2) > 0; t++) sim.Tick(None);   // auto-retarget mops up the cluster

        return (sim.Gold(1), sim.Stockpile(1, ResourceType.Wood), sim.Stockpile(1, ResourceType.Stone), sim.Faith(1));
    }

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        if (!ok) _failures++;
    }
}
