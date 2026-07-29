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

    static void Main()
    {
        Console.WriteLine("Victory — faith, the four paths, and the crown\n");

        FaithOpensAtTheStartingCongregation();
        AChurchConvertsTheRealm();
        TheRealmIsToldAtEightyPercent();
        ADualGoalTakesTheCrown();
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
        Seed(sim, 1, 4);
        sim.AddGold(1, 500_000);                          // banks the Economic MEDIUM (a once-goal)
        sim.PlaceBuilding(BuildingType.Church, 1, 20, 20);

        TickUntil(sim, () => sim.Faith(1) >= 75, 6000);
        Check("the half-million banks the Economic MEDIUM", sim.Progress(1, VictoryPath.Economic).MediumBanked);
        Check("no crown the instant the HIGH goal is first met", sim.VictoryOwner == -1);

        int need = Simulation.HoldTicksFor(VictoryPath.Religious);
        int used = TickUntil(sim, () => sim.VictoryOwner >= 0, need + 2000);
        Check("the dual goal takes the crown", sim.VictoryOwner == 1);
        Check("won by the path whose HIGH was held (Religious)", sim.VictoryPathIdx == (int)VictoryPath.Religious);
        Check("and only after a sustained hold, not on the first touch", used >= need - RealmIntervalGuess);
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
            Seed(sim, 1, 4);
            sim.AddGold(1, 500_000);
            sim.PlaceBuilding(BuildingType.Church, 1, 20, 20);
        }

        int cap = Simulation.HoldTicksFor(VictoryPath.Religious) + 4000;
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
