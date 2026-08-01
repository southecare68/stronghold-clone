// Victory — faith and the four scored paths (docs/victory-paths.md).
//
// Faith is the share of a realm's people won over to the church: it opens at a
// starting congregation and climbs as churches minister to more of the populace.
// On top of that sits the victory spine — a referee that, each realm tick, scores
// every owner on each path, tells the whole realm when one crosses 80% of a HIGH
// goal, makes a HIGH goal be HELD for a sustained window before it counts, and
// crowns anyone who holds one path's HIGH while having banked a DIFFERENT path's
// MEDIUM (the dual goal — no single-stat cheese).
//
// What these tests hold down: faith opens at its floor and rests there without a
// church, a church converts the realm past the HIGH goal, the 80% announcement
// fires exactly once, a dual goal (and only a dual goal, held to term) takes the
// crown, the territory/science seams read as designed — and, the one that matters
// for a match, two clients agree on the whole race and its winner.
//
// Sim-only, like the other economy suites. Run with `dotnet run`.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;
    static readonly Command[] None = Array.Empty<Command>();

    // The Religious branch from the trunk up to its capstone, in dependency order.
    static readonly int[] ReligiousToCapstone =
    {
        TechTree.Roads, TechTree.Chapel, TechTree.Shrine,
        TechTree.Missionaries, TechTree.Cathedral, TechTree.GrandTemple,
    };

    // The Economic branch (Monopoly fork) from the trunk up to its capstone.
    static readonly int[] EconomicToCapstone =
    {
        TechTree.Roads, TechTree.Market, TechTree.TradePost,
        TechTree.Monopoly, TechTree.BankingHouse, TechTree.GrandExchange,
    };

    static void Main()
    {
        Console.WriteLine("Victory — faith, the four paths, and the crown\n");

        FaithOpensAtTheStartingCongregation();
        AChurchConvertsTheRealm();
        TheRealmIsToldAtEightyPercent();
        ADualGoalTakesTheCrown();
        APopulationFloorGatesTheCrown();
        TheGameCalendarAdvances();
        PaceScaleStretchesTheMatch();
        TheTerritoryAndScienceSeams();
        TwoClientsAgreeOnTheCrown();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // A fresh realm opens with a congregation of believers, and with no church to
    // minister to anyone it simply rests there — faith is earned, not decayed.
    static void FaithOpensAtTheStartingCongregation()
    {
        Console.WriteLine("faith opens at its congregation and rests without a church:");
        var sim = Realm();
        Seed(sim, 1, 4);

        Check("opens at the starting share (25%)", sim.Faith(1) == 25);
        Ticks(sim, 2000);                 // many settles, no church built
        Check("and with no church it holds at the floor (25%)", sim.Faith(1) == 25);
    }

    // Raise a church and the flock grows: faith climbs off the floor and, once the
    // church's reach covers the small populace, past the 75% HIGH goal.
    static void AChurchConvertsTheRealm()
    {
        Console.WriteLine("a church converts the realm:");
        var sim = Realm();
        Seed(sim, 1, 4);
        int before = sim.Faith(1);
        sim.PlaceBuilding(BuildingType.Church, 1, 20, 20);

        TickUntil(sim, () => sim.Faith(1) >= 75, 6000);
        Check("a church lifts faith off the starting share", sim.Faith(1) > before);
        Check("and converts the realm past the HIGH goal (75%)", sim.Faith(1) >= 75);
    }

    // Cross 80% of a HIGH goal (60% faith is 80% of the 75% goal) and the whole
    // realm is told — exactly once, however far faith climbs on afterward.
    static void TheRealmIsToldAtEightyPercent()
    {
        Console.WriteLine("the realm is told at 80% of a goal:");
        var sim = Realm();
        Seed(sim, 1, 4);
        sim.PlaceBuilding(BuildingType.Church, 1, 20, 20);

        TickUntil(sim, () => sim.Faith(1) >= 60, 6000);   // 60% faith == 80% of the goal
        Ticks(sim, 40);                                   // let the realm tick past the crossing
        Check("the realm hears of the 80% crossing", Announces(sim) == 1);
        Check("the path reports it has announced", sim.Progress(1, VictoryPath.Religious).Announced);

        Ticks(sim, 2000);                                 // faith climbs on toward 100
        Check("but the realm is told only once", Announces(sim) == 1);
    }

    // The whole spine end to end: bank one path's MEDIUM, hold another path's HIGH
    // for its full sustained window, and the crown is claimed — but not a tick
    // before the window is served, and not without the second, different goal.
    static void ADualGoalTakesTheCrown()
    {
        Console.WriteLine("a dual goal, held to term, takes the crown:");
        var sim = Realm();
        GrowFaithfulRealm(sim);                           // 200 pop (over the win floor) + churches for that flock
        sim.AddGold(1, 500_000);                          // banks the Economic MEDIUM (a once-goal)

        TickUntil(sim, () => sim.Faith(1) >= 75, 6000);
        Check("the half-million banks the Economic MEDIUM", sim.Progress(1, VictoryPath.Economic).MediumBanked);
        // Capstone gate: 75% faith alone is not the crown — the tech tree gates the
        // Religious HIGH behind the Grand Temple.
        Check("75% faith is not the HIGH goal without the capstone", !sim.Progress(1, VictoryPath.Religious).HighMet);

        // Climb the Religious branch to its capstone; now the HIGH goal is live.
        sim.AddResearch(1, 1000);
        foreach (int id in ReligiousToCapstone) sim.TryResearch(1, id);
        Check("the Grand Temple capstone unlocks the HIGH goal", sim.Progress(1, VictoryPath.Religious).HighMet);
        Check("no crown the instant the HIGH goal is first met", sim.VictoryOwner == -1);

        int need = sim.HoldTicksFor(VictoryPath.Religious);
        int used = TickUntil(sim, () => sim.VictoryOwner >= 0, need + 2000);
        Check("the dual goal takes the crown", sim.VictoryOwner == 1);
        Check("won by the path whose HIGH was held (Religious)", sim.VictoryPathIdx == (int)VictoryPath.Religious);
        Check("and only after a sustained hold, not on the first touch", used >= need - RealmIntervalGuess);
    }

    // The population floor: no crown counts until the realm is a real kingdom (200
    // pop). A tiny settlement can meet a HIGH goal and bank a MEDIUM on paper, but
    // the crown's hold never even starts until it grows — then the very same
    // standing begins to count. Uses the Economic hoard as the HIGH because its
    // metric does not depend on population, so growing the realm doesn't disturb it.
    static void APopulationFloorGatesTheCrown()
    {
        Console.WriteLine("the population floor gates the crown:");
        var sim = Realm();
        Seed(sim, 1, 20);                                  // a hamlet, far under the floor

        // Bank a MEDIUM on one path while still small — it sticks forever after.
        sim.PlaceBuilding(BuildingType.Church, 1, 20, 20);
        sim.PlaceBuilding(BuildingType.Church, 1, 24, 20);  // reach 24 vs 20 souls → the flock converts fast
        TickUntil(sim, () => sim.Progress(1, VictoryPath.Religious).MediumBanked, 6000);
        Check("banks a MEDIUM while still a hamlet", sim.Progress(1, VictoryPath.Religious).MediumBanked);

        // Meet a pop-independent HIGH: the hoard, its branch climbed to the capstone.
        sim.AddGold(1, 100_000);
        sim.AddResearch(1, 2000);
        foreach (int id in EconomicToCapstone) sim.TryResearch(1, id);
        Check("the Economic HIGH is met (hoard + capstone)", sim.Progress(1, VictoryPath.Economic).HighMet);
        Check("but the realm is under the floor", !sim.PopulationFloorMet(1));

        // Under the floor the crown is held back — the hold never even starts.
        Ticks(sim, 10 * RealmIntervalGuess);
        Check("under the floor, no crown", sim.VictoryOwner == -1);
        Check("and the hold does not even begin", sim.Progress(1, VictoryPath.Economic).HoldTicks == 0);

        // Grow past the floor and the same standing starts to count.
        Seed(sim, 1, 200);                                 // now 220 pop, over the floor
        Check("now over the floor", sim.PopulationFloorMet(1));
        Ticks(sim, 4 * RealmIntervalGuess);
        Check("over the floor, the hold begins to accrue", sim.Progress(1, VictoryPath.Economic).HoldTicks > 0);
    }

    // The game calendar — a cosmetic clock off the shared tick count. A match opens
    // on Year 1, Month 1; a month is one TicksPerMonth, a year twelve of them. Purely
    // derived, so it never desyncs and never touches the checksum.
    static void TheGameCalendarAdvances()
    {
        Console.WriteLine("the game calendar advances with the tick:");
        var sim = new Simulation(TileMap.Open(32));
        Check("a match opens on Year 1, Month 1", sim.GameYear == 1 && sim.GameMonth == 1);

        int month = Simulation.TicksPerMonth;
        Ticks(sim, month);
        Check("one month on, it reads Month 2", sim.GameYear == 1 && sim.GameMonth == 2);

        Ticks(sim, month * 11);                       // eleven more months → the new year
        Check("twelve months on, it rolls to Year 2, Month 1", sim.GameYear == 2 && sim.GameMonth == 1);

        Check("and the month has a name", sim.GameMonthName == "January");
    }

    // The match-length dial: one knob (PaceScale) stretches the victory holds and the
    // research cost together, so a game can run brisk (1×) or epic (~2 hours at 6×).
    // Defaults to 1, so every other test runs at full speed.
    static void PaceScaleStretchesTheMatch()
    {
        Console.WriteLine("the pace dial stretches holds and research together:");
        var brisk = new Simulation(TileMap.Open(32));                     // PaceScale 1 (default)
        var epic  = new Simulation(TileMap.Open(32)) { PaceScale = 6 };
        Check("the default is the brisk 1× pace", brisk.PaceScale == 1);
        Check("a longer pace multiplies every hold window 6×",
              epic.HoldTicksFor(VictoryPath.Religious) == 6 * brisk.HoldTicksFor(VictoryPath.Religious)
           && epic.HoldTicksFor(VictoryPath.Economic)  == 6 * brisk.HoldTicksFor(VictoryPath.Economic));
        Check("and research costs 6× as much to climb",
              epic.ResearchCostFor(1, TechTree.Roads) == 6 * brisk.ResearchCostFor(1, TechTree.Roads)
           && epic.ResearchCostFor(1, TechTree.GrandTemple) == 6 * brisk.ResearchCostFor(1, TechTree.GrandTemple));
    }

    // The two clauses that wait on later phases: territory (Phase 3) and science
    // (Phase 4). Both are wired so the HUD and spies have their slots, and both
    // read exactly as designed at a one-keep, no-research start.
    static void TheTerritoryAndScienceSeams()
    {
        Console.WriteLine("the territory and science seams read as designed:");
        var sim = Realm();
        Seed(sim, 1, 4);

        Check("one keep is one territory (the multi-territory seam)", sim.TerritoryCount(1) == 1);
        Check("Domain's HIGH is gated on territory, so unmet at one keep",
              !sim.Progress(1, VictoryPath.Domain).HighMet);
        var sci = sim.Progress(1, VictoryPath.Science);
        Check("Science is a wired stub until its tech tree exists", sci.HighPercent == 0 && !sci.HighMet);
    }

    // The property that matters for a match: two clients running the identical race
    // stay byte-identical the whole way and crown the same winner by the same path.
    static void TwoClientsAgreeOnTheCrown()
    {
        Console.WriteLine("two clients agree on the crown:");
        var a = Realm();
        var b = Realm();
        foreach (var sim in new[] { a, b })
        {
            GrowFaithfulRealm(sim);                                          // identical on both — deterministic seed & placement
            sim.AddGold(1, 500_000);
            sim.AddResearch(1, 1000);
            foreach (int id in ReligiousToCapstone) sim.TryResearch(1, id);   // unlock the Religious HIGH on both clients
        }

        int cap = a.HoldTicksFor(VictoryPath.Religious) + 4000;
        bool synced = true;
        int t = 0;
        for (; t < cap && a.VictoryOwner < 0; t++)
        {
            a.Tick(None);
            b.Tick(None);
            if (a.StateChecksum() != b.StateChecksum()) synced = false;
        }

        Check($"StateChecksum identical across the whole race ({t} ticks)", synced);
        Check("both clients crowned the same winner", a.VictoryOwner == b.VictoryOwner && a.VictoryOwner == 1);
        Check("and by the same path", a.VictoryPathIdx == b.VictoryPathIdx);
    }

    // ---- helpers ---------------------------------------------------------

    // A lone realm: one keep, and a deep larder so a long hold never starves it.
    static Simulation Realm()
    {
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 4, 4);
        sim.AddResource(1, ResourceType.Food, 200_000);
        return sim;
    }

    // The realm cadence, used only as a loose lower bound on the hold window (the
    // hold accrues one realm-interval at a time, so the win can land at most one
    // interval early relative to the exact term).
    const int RealmIntervalGuess = 40;

    // A realm grown past the population win-floor (200) and faithful enough to still
    // take the Religious crown at that larger scale. No housing is built on purpose:
    // a full larder keeps approval high so the 200 seeded peasants neither breed
    // beyond the keep's cap nor emigrate, pinning the population for the long hold.
    // Faith is a share of the WHOLE flock, so a bigger realm needs more churches —
    // fourteen minister to ~168 souls, three-quarters of two hundred and then some.
    static void GrowFaithfulRealm(Simulation sim)
    {
        Seed(sim, 1, 200);
        sim.AddResource(1, ResourceType.Food, 5_000_000);      // a deep larder for the ~12-minute vigil
        for (int i = 0; i < 14; i++)
            sim.PlaceBuilding(BuildingType.Church, 1, 8 + (i % 7) * 3, 20 + (i / 7) * 3);
    }

    static int Announces(Simulation sim)
    {
        int n = 0;
        foreach (var e in sim.VictoryEvents)
            if (e.Kind == VictoryEventKind.Approaching && e.Path == VictoryPath.Religious && e.Owner == 1) n++;
        return n;
    }

    static void Ticks(Simulation sim, int n) { for (int i = 0; i < n; i++) sim.Tick(None); }
    static void Seed(Simulation sim, int owner, int n) { for (int i = 0; i < n; i++) sim.SpawnPeasant(owner); }

    // Tick until a condition holds or a tick cap is hit; returns the ticks spent.
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
