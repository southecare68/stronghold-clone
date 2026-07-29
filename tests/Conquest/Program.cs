// Conquest — taking a keep by force (the Domain war-tool).
//
// A keep struck down by an attacker who has researched Conquest is ANNEXED, not
// razed: it stands, battered, under its new lord; the territory becomes theirs
// (feeding the Domain "5 territories" clause by force), and the old owner's idle
// folk near it change hands (the population payoff). Without the tech, a felled
// keep simply falls, as it always did.
//
// What these tests hold down: without Conquest a keep is razed; with it the keep is
// annexed and becomes a new territory; the conquered population changes hands; and
// two clients agree on the whole assault bit-for-bit.
//
// Sim-only, like the other economy suites. Run with `dotnet run`.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;
    static readonly Command[] None = Array.Empty<Command>();

    static readonly int[] ConquestChain =
    {
        TechTree.Roads, TechTree.Farmstead, TechTree.Husbandry,
        TechTree.Homesteads, TechTree.ProvincialKeeps, TechTree.Conquest,
    };

    static void Main()
    {
        Console.WriteLine("Conquest — taking a keep by force\n");

        WithoutConquestAKeepIsRazed();
        WithConquestAKeepIsAnnexed();
        ConquestAnnexesThePopulation();
        TwoClientsAgreeOnTheAssault();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // The old rule stands when you haven't researched Conquest: the keep falls.
    static void WithoutConquestAKeepIsRazed()
    {
        Console.WriteLine("without Conquest a keep is razed:");
        var sim = Field(out var enemyKeep);
        Besiege(sim, enemyKeep, 12);
        RunUntil(sim, () => !enemyKeep.Alive, 3000);
        Check("the enemy keep is razed", !enemyKeep.Alive && sim.TerritoryCount(2) == 0);
        Check("and it did not become your territory", sim.TerritoryCount(1) == 1);
    }

    // With Conquest, the killing blow annexes instead of razing.
    static void WithConquestAKeepIsAnnexed()
    {
        Console.WriteLine("with Conquest a keep is annexed:");
        var sim = Field(out var enemyKeep);
        Research(sim, 1, ConquestChain);
        Check("one territory before the assault", sim.TerritoryCount(1) == 1);

        Besiege(sim, enemyKeep, 12);
        RunUntil(sim, () => enemyKeep.Owner == 1, 3000);
        Check("the enemy keep is annexed, still standing", enemyKeep.Owner == 1 && enemyKeep.Alive);
        Check("and it becomes a second territory", sim.TerritoryCount(1) == 2);
        Check("the old owner has lost it", sim.TerritoryCount(2) == 0);
    }

    // The conquered keep carries its idle population to the conqueror.
    static void ConquestAnnexesThePopulation()
    {
        Console.WriteLine("conquest annexes the population:");
        var sim = Field(out var enemyKeep);
        sim.AddResource(2, ResourceType.Food, 20000);   // keep the defender's folk fed and put, not starving off
        Research(sim, 1, ConquestChain);

        var theirFolk = new List<int>();
        for (int i = 0; i < 6; i++) theirFolk.Add(sim.SpawnPeasant(2).Id);

        Besiege(sim, enemyKeep, 12);
        RunUntil(sim, () => enemyKeep.Owner == 1, 3000);

        int changedHands = 0;
        foreach (int id in theirFolk)
            foreach (var u in sim.Units)
                if (u.Id == id && u.Alive && u.Owner == 1) { changedHands++; break; }
        Check("the conquered peasants now serve their new lord", changedHands > 0);
    }

    static void TwoClientsAgreeOnTheAssault()
    {
        Console.WriteLine("two clients agree on the assault:");
        var a = Field(out var keepA);
        var b = Field(out var keepB);
        foreach (var (sim, keep) in new[] { (a, keepA), (b, keepB) })
        {
            Research(sim, 1, ConquestChain);
            Besiege(sim, keep, 12);
        }

        bool synced = true;
        for (int t = 0; t < 3000 && keepA.Owner == 2; t++)
        {
            a.Tick(None);
            b.Tick(None);
            if (a.StateChecksum() != b.StateChecksum()) synced = false;
        }
        Check("StateChecksum identical through the siege", synced);
        Check("both annexed the keep the same way", keepA.Owner == 1 && keepB.Owner == 1);
    }

    // ---- helpers ---------------------------------------------------------

    // Your keep and a rival's, on open ground, with a purse and a research fund.
    static Simulation Field(out Building enemyKeep)
    {
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 4, 4);
        enemyKeep = sim.PlaceBuilding(BuildingType.Keep, 2, 30, 30);
        return sim;
    }

    static void Research(Simulation sim, int owner, int[] chain)
    {
        sim.AddResearch(owner, 3000);
        foreach (int id in chain) sim.TryResearch(owner, id);
    }

    static void Besiege(Simulation sim, Building keep, int n)
    {
        var ids = new int[n];
        for (int i = 0; i < n; i++) ids[i] = sim.SpawnUnit(1, 26 + i % 4, 27 + i % 3).Id;
        sim.Tick(new[] { new Command { Owner = 1, Type = CommandType.AttackBuilding, UnitIds = ids, TargetId = keep.Id } });
    }

    static void RunUntil(Simulation sim, Func<bool> done, int cap)
    {
        for (int i = 0; i < cap && !done(); i++) sim.Tick(None);
    }

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        if (!ok) _failures++;
    }
}
