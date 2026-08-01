// Exile & Return — you cannot kill the king.
//
// When a seated realm loses its LAST keep it is not eliminated (there is no
// last-keep-standing win — you win by a crown). Instead the king flees into exile:
// the fallen territory is razed and the realm reset to a bare opening, but its
// RESEARCHED knowledge and any banked MEDIUM survive; after a regroup a fresh keep
// and starter camp rise at the map's most isolated corner.
//
// What these tests pin down: losing the last keep exiles rather than ends you; the
// fallen realm is razed and reset; knowledge and a banked medium carry over; the
// refounded keep sits away from enemies; a never-seated owner is left alone; and —
// the one that matters for a match — two machines exile and return in lockstep.
//
// Sim-only. Run with `dotnet run`.

using System;
using System.Collections.Generic;
using Sim;
using Netcode;

static class Program
{
    static int _failures;
    static readonly List<Command> None = new();

    static void Main()
    {
        Console.WriteLine("Exile & Return — the king endures\n");

        LosingYourLastKeepExilesYouNotEndsYou();
        ExileRazesTheRealmAndResetsIt();
        KnowledgeAndBankedMediumSurviveExile();
        TheKingRefoundsAwayFromEnemies();
        ANeverSeatedOwnerIsLeftAlone();
        TwoClientsAgreeThroughExileAndReturn();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // The heart of it: raze the last keep and the realm doesn't die — it goes keepless
    // for a spell, then a new keep rises on its own.
    static void LosingYourLastKeepExilesYouNotEndsYou()
    {
        Console.WriteLine("losing your last keep exiles you, it does not end you:");
        var sim = new Simulation(TileMap.Open(64));
        var keep = sim.PlaceBuilding(BuildingType.Keep, 1, 6, 6);
        sim.Tick(None);                                   // seats the realm (marks it ever-seated)
        Check("the realm is seated", sim.TerritoryCount(1) == 1);

        keep.Hp = 0;                                      // the keep is battered down
        sim.Tick(None);                                   // swept, then exile begins
        Check("its last keep is gone", sim.TerritoryCount(1) == 0);

        for (int i = 0; i < 360 && sim.TerritoryCount(1) == 0; i++) sim.Tick(None);
        Check("and after the regroup a new keep rises", sim.TerritoryCount(1) == 1);
        Check("with a starter camp of peasants", sim.PeasantCount(1) >= 3);
    }

    // Exile razes what is left of the territory and resets the treasury to a bare
    // opening kit — a brutal setback.
    static void ExileRazesTheRealmAndResetsIt()
    {
        Console.WriteLine("\nexile razes the fallen realm and resets it:");
        var sim = new Simulation(TileMap.Open(64));
        var keep = sim.PlaceBuilding(BuildingType.Keep, 1, 6, 6);
        sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 12, 12);
        sim.PlaceBuilding(BuildingType.Farm, 1, 18, 18);
        sim.AddResource(1, ResourceType.Wood, 500);
        sim.AddGold(1, 5000);
        sim.Tick(None);
        Check("the realm has several buildings", sim.BuildingList.Count >= 3);

        keep.Hp = 0;
        sim.Tick(None);                                   // exile begins this tick
        Check("every building of the fallen realm is razed", NoneOwnedBy(sim, 1));
        Check("the treasury is emptied", sim.Gold(1) == 0);
        Check("resources reset to a starter kit", sim.Stockpile(1, ResourceType.Wood) < 500 && sim.Stockpile(1, ResourceType.Food) > 0);
    }

    // The comeback has teeth: researched tech and a banked MEDIUM survive the fall, so
    // an exiled lord keeps their knowledge and half their dual goal.
    static void KnowledgeAndBankedMediumSurviveExile()
    {
        Console.WriteLine("\nknowledge and a banked medium survive exile:");
        var sim = new Simulation(TileMap.Open(64));
        var keep = sim.PlaceBuilding(BuildingType.Keep, 1, 6, 6);
        sim.AddResearch(1, 1000);
        sim.TryResearch(1, TechTree.Roads);
        sim.TryResearch(1, TechTree.Market);
        Check("the branch was researched", sim.IsTechResearched(1, TechTree.Market));

        sim.AddGold(1, 40_000);                           // banks the Economic MEDIUM (35k, once)
        sim.Tick(None);
        for (int i = 0; i < 45; i++) sim.Tick(None);      // a realm tick, so the medium latches
        Check("the Economic medium is banked", sim.Progress(1, VictoryPath.Economic).MediumBanked);

        keep.Hp = 0;
        sim.Tick(None);                                   // exile
        Check("researched knowledge survives the fall", sim.IsTechResearched(1, TechTree.Market));
        Check("the banked medium survives the fall", sim.Progress(1, VictoryPath.Economic).MediumBanked);
        Check("but the treasury did not", sim.Gold(1) == 0);
    }

    // The king refounds at the safest corner — far from any standing keep, not next
    // door to the rival who just sacked them.
    static void TheKingRefoundsAwayFromEnemies()
    {
        Console.WriteLine("\nthe king refounds away from enemies:");
        var sim = new Simulation(TileMap.Open(64));
        var mine = sim.PlaceBuilding(BuildingType.Keep, 1, 6, 6);
        var foe = sim.PlaceBuilding(BuildingType.Keep, 2, 12, 12);   // a rival keep nearby (clear of mine's footprint)
        sim.Tick(None);
        Check("both keeps stand", mine != null && foe != null);

        mine.Hp = 0;
        sim.Tick(None);
        for (int i = 0; i < 360 && sim.TerritoryCount(1) == 0; i++) sim.Tick(None);
        Check("a new keep rose", sim.TerritoryCount(1) == 1);

        var refounded = FindKeep(sim, 1);
        long dx = refounded.CenterX - foe.CenterX, dy = refounded.CenterY - foe.CenterY;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        Check($"and it stands well clear of the rival ({dist:0} tiles off)", dist > 30);
    }

    // A stock entry with no keep and no history (a setup call before any keep) is not
    // a realm in play, so it is never dragged into exile.
    static void ANeverSeatedOwnerIsLeftAlone()
    {
        Console.WriteLine("\na never-seated owner is left alone:");
        var sim = new Simulation(TileMap.Open(48));
        sim.AddGold(1, 500);                              // gives owner 1 a stock entry, but no keep
        for (int i = 0; i < 400; i++) sim.Tick(None);
        Check("no phantom keep is founded for a never-seated owner", sim.TerritoryCount(1) == 0);
        Check("and its gold is untouched", sim.Gold(1) == 500);
    }

    // The one that matters most: two clients must exile and refound identically —
    // same razing, same reset, same refound site — every tick.
    static void TwoClientsAgreeThroughExileAndReturn()
    {
        Console.WriteLine("\ntwo clients agree through exile and return:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(64));
        var b = new Client(2, net, TileMap.Open(64));
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.PlaceBuilding(BuildingType.Keep, 1, 6, 6);
            c.Sim.PlaceBuilding(BuildingType.Keep, 2, 56, 56);
        }
        a.SendInput(); b.SendInput(); a.TryStep(); b.TryStep();     // seat both

        foreach (var c in new[] { a, b })                          // batter owner 1's keep on both, identically
            foreach (var bld in c.Sim.BuildingList)
                if (bld.Owner == 1 && bld.Type == BuildingType.Keep) bld.Hp = 0;

        int desyncs = 0, first = -1;
        for (int t = 0; t < 420; t++)
        {
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical through exile and return" + (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("and owner 1 refounded on both", a.Sim.TerritoryCount(1) == 1 && b.Sim.TerritoryCount(1) == 1);
    }

    // ---- helpers -----------------------------------------------------------

    static bool NoneOwnedBy(Simulation sim, int owner)
    {
        foreach (var b in sim.BuildingList) if (b.Owner == owner) return false;
        return true;
    }

    static Building FindKeep(Simulation sim, int owner)
    {
        foreach (var b in sim.BuildingList) if (b.Alive && b.Owner == owner && b.Type == BuildingType.Keep) return b;
        return null;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
