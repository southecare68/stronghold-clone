// Spy — the counter-web (spy.pdf).
//
// Each spy is the dedicated answer to one crown — the only thing that pushes a
// rival's announced metric backward — plus the Assassin against whoever leans
// hardest on force. A spy is trained by researching its War-branch node, costs
// gold, sits on a cooldown, and its bite is blunted by the target's OWN Tier-III
// counter (the opportunity cost that makes being targeted survivable).
//
// What these tests hold down: a spy needs training and a real rival; each of the
// five effects lands (drain gold, push faith back, wreck a wonder, incite
// emigration, cut down a soldier); each is softened (or blocked) by the matching
// counter; the cooldown holds; and two clients raise the same daggers bit-for-bit.
//
// Sim-only, like the other economy suites. Run with `dotnet run`.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;
    static readonly Command[] None = Array.Empty<Command>();

    static void Main()
    {
        Console.WriteLine("Spy — the counter-web\n");

        SpiesNeedTrainingAndARival();
        TheEmbezzlerSkimsTheHoard();
        TheInquisitorPushesFaithBack();
        TheSaboteurWrecksAWonder();
        TheAgitatorIncitesEmigration();
        TheAssassinCutsDownASoldier();
        SpiesSitOnACooldown();
        TwoClientsAgreeOnEspionage();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void SpiesNeedTrainingAndARival()
    {
        Console.WriteLine("a spy needs training and a real rival:");
        var sim = TwoRealms();
        Check("an untrained spy is refused", !sim.CanSpy(1, TechTree.Embezzler, 2));
        Train(sim, 1, TechTree.Embezzler);
        Check("a trained spy against a rival is ready", sim.CanSpy(1, TechTree.Embezzler, 2));
        Check("but never against yourself", !sim.CanSpy(1, TechTree.Embezzler, 1));
        Check("the rival is the other keep-holder", sim.FirstRival(1) == 2);
    }

    static void TheEmbezzlerSkimsTheHoard()
    {
        Console.WriteLine("the Embezzler skims the hoard (Vault resists):");
        var sim = TwoRealms();
        sim.AddGold(2, 1000);
        Train(sim, 1, TechTree.Embezzler);
        int mine0 = sim.Gold(1);
        sim.TrySpy(1, TechTree.Embezzler, 2);
        Check("the target's hoard is drained", sim.Gold(2) < 1000);
        Check("and the loot funds your treasury", sim.Gold(1) > mine0);

        var vault = TwoRealms();
        vault.AddGold(2, 1000);
        Research(vault, 2, EconChain);   // Banking House = the Vault
        Train(vault, 1, TechTree.Embezzler);
        vault.TrySpy(1, TechTree.Embezzler, 2);
        Check("a Vault blunts the skim", vault.Gold(2) > sim.Gold(2));
    }

    static void TheInquisitorPushesFaithBack()
    {
        Console.WriteLine("the Inquisitor pushes faith back (Inquisition resists):");
        var sim = TwoRealms();
        Train(sim, 1, TechTree.Inquisitor);
        int faith0 = sim.Faith(2);
        sim.TrySpy(1, TechTree.Inquisitor, 2);
        Check("faith is knocked backward", sim.Faith(2) < faith0);

        var cath = TwoRealms();
        Research(cath, 2, RelChain);   // Cathedral = the Inquisition
        Train(cath, 1, TechTree.Inquisitor);
        cath.TrySpy(1, TechTree.Inquisitor, 2);
        Check("the Inquisition blunts the discredit", cath.Faith(2) > sim.Faith(2));
    }

    static void TheSaboteurWrecksAWonder()
    {
        Console.WriteLine("the Saboteur wrecks a wonder:");
        var sim = TwoRealms();
        sim.AddResource(2, ResourceType.Wood, 3000);
        sim.AddResource(2, ResourceType.Stone, 3000);
        Research(sim, 2, SciChain);   // to the Academy, so wonders can be raised
        Build(sim, 2, BuildingType.Wonder, 20, 20);
        Check("the target raised a wonder", sim.CountBuildings(2, BuildingType.Wonder) == 1);

        Train(sim, 1, TechTree.Saboteur);
        sim.TrySpy(1, TechTree.Saboteur, 2);
        Check("one operation damages the wonder", WonderHp(sim, 2) < 500 && sim.CountBuildings(2, BuildingType.Wonder) == 1);
        Ticks(sim, sim.SpyReadyIn(1, TechTree.Saboteur) + 1);
        sim.TrySpy(1, TechTree.Saboteur, 2);
        Check("a second razes it, cutting the wonder count", sim.CountBuildings(2, BuildingType.Wonder) == 0);
    }

    static void TheAgitatorIncitesEmigration()
    {
        Console.WriteLine("the Agitator incites emigration (Festival Hall resists):");
        var sim = TwoRealms();
        for (int i = 0; i < 6; i++) sim.SpawnPeasant(2);
        Train(sim, 1, TechTree.Agitator);
        int pop0 = sim.PeasantCount(2);
        sim.TrySpy(1, TechTree.Agitator, 2);
        int lost = pop0 - sim.PeasantCount(2);
        Check("peasants pack up and leave", lost > 0);

        var fest = TwoRealms();
        for (int i = 0; i < 6; i++) fest.SpawnPeasant(2);
        Research(fest, 2, DomChain);   // Provincial Keeps = the Festival Hall
        Train(fest, 1, TechTree.Agitator);
        int fpop0 = fest.PeasantCount(2);
        fest.TrySpy(1, TechTree.Agitator, 2);
        Check("the Festival Hall keeps more of them home", (fpop0 - fest.PeasantCount(2)) < lost);
    }

    static void TheAssassinCutsDownASoldier()
    {
        Console.WriteLine("the Assassin cuts down a soldier (Bodyguard blocks):");
        var sim = TwoRealms();
        sim.SpawnUnit(2, 32, 32);   // a soldier (not a peasant)
        Train(sim, 1, TechTree.Assassin);
        int army0 = sim.ArmySize(2);
        sim.TrySpy(1, TechTree.Assassin, 2);
        // the kill is swept by the dead-unit pass on the next tick
        sim.Tick(None);
        Check("a soldier falls to the Assassin", sim.ArmySize(2) < army0);

        var guarded = TwoRealms();
        guarded.SpawnUnit(2, 32, 32);
        Research(guarded, 2, new[] { TechTree.Roads, TechTree.Muster, TechTree.Bodyguard });
        Train(guarded, 1, TechTree.Assassin);
        int garmy0 = guarded.ArmySize(2);
        guarded.TrySpy(1, TechTree.Assassin, 2);
        guarded.Tick(None);
        Check("a Bodyguard turns the blade", guarded.ArmySize(2) == garmy0);
    }

    static void SpiesSitOnACooldown()
    {
        Console.WriteLine("a spy sits on a cooldown:");
        var sim = TwoRealms();
        sim.AddGold(2, 5000);
        Train(sim, 1, TechTree.Embezzler);
        Check("ready at first", sim.CanSpy(1, TechTree.Embezzler, 2));
        sim.TrySpy(1, TechTree.Embezzler, 2);
        Check("spent, it goes on cooldown", !sim.CanSpy(1, TechTree.Embezzler, 2));
        Ticks(sim, sim.SpyReadyIn(1, TechTree.Embezzler) + 1);
        Check("and comes back after the cooldown", sim.CanSpy(1, TechTree.Embezzler, 2));
    }

    static void TwoClientsAgreeOnEspionage()
    {
        Console.WriteLine("two clients agree on espionage:");
        var a = TwoRealms();
        var b = TwoRealms();
        foreach (var sim in new[] { a, b })
        {
            sim.AddGold(2, 1000);
            for (int i = 0; i < 6; i++) sim.SpawnPeasant(2);
            Train(sim, 1, TechTree.Embezzler);
            Train(sim, 1, TechTree.Agitator);
        }

        bool synced = true;
        var cmds = new[]
        {
            new Command { Owner = 1, Type = CommandType.Spy, TargetId = TechTree.Embezzler, X = 2 },
            new Command { Owner = 1, Type = CommandType.Spy, TargetId = TechTree.Agitator, X = 2 },
        };
        foreach (var c in cmds)
        {
            var one = new[] { c };
            a.Tick(one);
            b.Tick(one);
            if (a.StateChecksum() != b.StateChecksum()) synced = false;
        }
        for (int t = 0; t < 200; t++) { a.Tick(None); b.Tick(None); if (a.StateChecksum() != b.StateChecksum()) synced = false; }
        Check("StateChecksum identical through the operations", synced);
        Check("both raided the same gold", a.Gold(2) == b.Gold(2) && a.Gold(2) < 1000);
    }

    // ---- helpers ---------------------------------------------------------

    static readonly int[] EconChain = { TechTree.Roads, TechTree.Market, TechTree.TradePost, TechTree.Monopoly, TechTree.BankingHouse };
    static readonly int[] RelChain  = { TechTree.Roads, TechTree.Chapel, TechTree.Shrine, TechTree.Missionaries, TechTree.Cathedral };
    static readonly int[] SciChain  = { TechTree.Roads, TechTree.ScholarsHut, TechTree.Library, TechTree.Engineering, TechTree.PrintingPress, TechTree.Academy };
    static readonly int[] DomChain  = { TechTree.Roads, TechTree.Farmstead, TechTree.Husbandry, TechTree.Homesteads, TechTree.ProvincialKeeps };

    // Two rival realms, and a purse for the spymaster (owner 1).
    static Simulation TwoRealms()
    {
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 4, 4);
        sim.PlaceBuilding(BuildingType.Keep, 2, 30, 30);
        sim.AddGold(1, 5000);
        return sim;
    }

    static void Train(Simulation sim, int owner, int spy)
        => Research(sim, owner, new[] { TechTree.Roads, TechTree.Muster, TechTree.SpyGuild, spy });

    static void Research(Simulation sim, int owner, int[] chain)
    {
        sim.AddResearch(owner, 3000);
        foreach (int id in chain) sim.TryResearch(owner, id);
    }

    static void Build(Simulation sim, int owner, BuildingType t, int x, int y)
        => sim.Tick(new[] { new Command { Owner = owner, Type = CommandType.Build, TargetId = (int)t, X = x, Y = y } });

    static int WonderHp(Simulation sim, int owner)
    {
        foreach (var b in sim.Buildings)
            if (b.Alive && b.Owner == owner && b.Type == BuildingType.Wonder) return b.Hp;
        return 0;
    }

    static void Ticks(Simulation sim, int n) { for (int i = 0; i < n; i++) sim.Tick(None); }

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        if (!ok) _failures++;
    }
}
