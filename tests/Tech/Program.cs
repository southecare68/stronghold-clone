// Tech — the research economy and the tech web (docs/victory-paths.md).
//
// "The tree IS the victory structure." Research banks every realm tick; you spend
// it, node by node, to climb a branch. Prereqs and a tier fork shape the path, a
// pick-limit caps capstones at one, and an escalating cross-branch cost makes a
// second branch dear — so you can afford one branch to its capstone plus a shallow
// dip into another, which is exactly the dual-goal. A branch's capstone is what
// unlocks its HIGH victory goal.
//
// What these tests hold down: research accrues (and Roads speeds it), prereqs and
// the fork gate what you may take, a second branch costs more the deeper you went
// in the first, the Grand Temple capstone unlocks the Religious HIGH, the Research
// command takes through the normal path, and two clients researching agree
// bit-for-bit.
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
        Console.WriteLine("Tech — research, the web, and the capstone gate\n");

        ResearchAccruesAndRoadsSpeedsIt();
        PrereqsAndTheForkGateTheBranch();
        ASecondBranchCostsMore();
        TheCapstoneUnlocksTheHighGoal();
        TheResearchCommandTakes();
        TwoClientsAgreeOnResearch();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // A realm banks research every realm tick, and Roads raises the pace.
    static void ResearchAccruesAndRoadsSpeedsIt()
    {
        Console.WriteLine("research accrues, and Roads speeds it:");
        var sim = Realm();
        int pace0 = sim.ResearchPace(1);
        Ticks(sim, 400);
        Check("a realm banks research over time", sim.ResearchPoints(1) > 0);

        sim.AddResearch(1, 100);
        Check("Roads can be researched", sim.TryResearch(1, TechTree.Roads));
        Check("and Roads raises the research pace", sim.ResearchPace(1) > pace0);
    }

    // The branch is a chain: Chapel needs Roads, Shrine needs Chapel; the Holy Order
    // fork is one-of-two and its pick is required before the Cathedral.
    static void PrereqsAndTheForkGateTheBranch()
    {
        Console.WriteLine("prereqs and the fork gate the branch:");
        var sim = Realm();
        sim.AddResearch(1, 1000);

        Check("Shrine is refused before its prereqs", !sim.CanResearch(1, TechTree.Shrine));
        Check("Chapel is refused before Roads", !sim.CanResearch(1, TechTree.Chapel));
        sim.TryResearch(1, TechTree.Roads);
        sim.TryResearch(1, TechTree.Chapel);
        Check("Shrine opens once Chapel is up", sim.TryResearch(1, TechTree.Shrine));

        // The Cathedral needs a Holy Order pick; taking one fork closes the other.
        Check("Cathedral is refused before a fork pick", !sim.CanResearch(1, TechTree.Cathedral));
        Check("Missionaries can be taken at the fork", sim.TryResearch(1, TechTree.Missionaries));
        Check("its sibling Zealotry is now closed", !sim.CanResearch(1, TechTree.Zealotry));
        Check("and the Cathedral opens", sim.CanResearch(1, TechTree.Cathedral));
    }

    // The escalating cross-branch cost: a node in a second branch costs more for
    // every scored node already taken in the first. Trade Post (Economic) is cheap
    // with nothing else, and dearer once a Religious node is banked.
    static void ASecondBranchCostsMore()
    {
        Console.WriteLine("a second branch costs more the deeper you went in the first:");
        var sim = Realm();
        sim.AddResearch(1, 1000);

        int baseCost = sim.ResearchCostFor(1, TechTree.TradePost);
        sim.TryResearch(1, TechTree.Roads);      // trunk — not a scored branch, no penalty
        sim.TryResearch(1, TechTree.Market);     // trunk
        Check("a trunk node adds no cross-branch penalty", sim.ResearchCostFor(1, TechTree.TradePost) == baseCost);

        sim.TryResearch(1, TechTree.Chapel);
        sim.TryResearch(1, TechTree.Shrine);     // one scored Religious node
        Check("a Religious node makes the Economic node dearer",
              sim.ResearchCostFor(1, TechTree.TradePost) > baseCost);
    }

    // A branch's capstone unlocks its HIGH victory goal: 75% faith is not the crown
    // until the Grand Temple stands.
    static void TheCapstoneUnlocksTheHighGoal()
    {
        Console.WriteLine("the capstone unlocks the HIGH goal:");
        var sim = Realm();
        Seed(sim, 1, 4);
        sim.PlaceBuilding(BuildingType.Church, 1, 20, 20);
        TickUntil(sim, () => sim.Faith(1) >= 75, 6000);

        Check("75% faith alone is not the HIGH goal", !sim.Progress(1, VictoryPath.Religious).HighMet);
        sim.AddResearch(1, 1000);
        foreach (int id in Plan) sim.TryResearch(1, id);
        Check("the Grand Temple is the capstone", TechTree.CapstoneFor(TechBranch.Religious) == TechTree.GrandTemple);
        Check("researched, it unlocks the HIGH goal", sim.Progress(1, VictoryPath.Religious).HighMet);
    }

    // Research also takes through the ordinary command path, charged and validated
    // exactly like a human clicking a node.
    static void TheResearchCommandTakes()
    {
        Console.WriteLine("the Research command takes:");
        var sim = Realm();
        sim.AddResearch(1, 100);
        sim.Tick(new[] { new Command { Owner = 1, Type = CommandType.Research, X = TechTree.Roads } });
        Check("a Research command researches its node", sim.IsTechResearched(1, TechTree.Roads));

        int before = sim.ResearchPoints(1);
        sim.Tick(new[] { new Command { Owner = 1, Type = CommandType.Research, X = TechTree.Shrine } });
        Check("an illegal node (prereqs unmet) is refused", !sim.IsTechResearched(1, TechTree.Shrine));
        Check("and a refused research spends nothing", sim.ResearchPoints(1) == before);
    }

    // Two clients researching the same nodes stay bit-for-bit identical.
    static void TwoClientsAgreeOnResearch()
    {
        Console.WriteLine("two clients agree on research:");
        var a = Realm();
        var b = Realm();
        foreach (var sim in new[] { a, b }) sim.AddResearch(1, 1000);

        var cmds = new List<Command>();
        foreach (int id in Plan) cmds.Add(new Command { Owner = 1, Type = CommandType.Research, X = id });

        bool synced = true;
        for (int i = 0; i < cmds.Count; i++)
        {
            var one = new[] { cmds[i] };
            a.Tick(one);
            b.Tick(one);
            if (a.StateChecksum() != b.StateChecksum()) synced = false;
        }
        Check("StateChecksum identical through the whole branch", synced);
        Check("both clients reached the capstone", a.IsTechResearched(1, TechTree.GrandTemple)
                                                   && b.IsTechResearched(1, TechTree.GrandTemple));
    }

    // ---- helpers ---------------------------------------------------------

    static readonly int[] Plan =
    {
        TechTree.Roads, TechTree.Chapel, TechTree.Shrine,
        TechTree.Missionaries, TechTree.Cathedral, TechTree.GrandTemple,
    };

    static Simulation Realm()
    {
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 4, 4);
        sim.AddResource(1, ResourceType.Food, 200_000);
        return sim;
    }

    static void Ticks(Simulation sim, int n) { for (int i = 0; i < n; i++) sim.Tick(None); }
    static void Seed(Simulation sim, int owner, int n) { for (int i = 0; i < n; i++) sim.SpawnPeasant(owner); }
    static int TickUntil(Simulation sim, Func<bool> done, int cap)
    {
        int t = 0;
        for (; t < cap && !done(); t++) sim.Tick(None);
        return t;
    }

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        if (!ok) _failures++;
    }
}
