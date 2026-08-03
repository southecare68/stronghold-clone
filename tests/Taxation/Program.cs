// Taxation — the realm loop: tax, rations, popularity, migration.
//
// Every RealmInterval a keep-holder runs one realm step: it takes tax as gold,
// feeds its people from the larder at the ordered ration, settles those two into
// a single popularity number, and lets that number draw newcomers in or send
// idlers off. The knobs are two commands, SetTax and SetRations; everything else
// is a consequence the player reads off popularity.
//
// What these tests hold down: gold fills and a bribe never overdraws the
// treasury, rations actually eat the larder, a hungry realm is punished no matter
// how generous the order, popularity moves the population the right way, the two
// commands take and clamp, unhappiness costs you idlers but never your working
// hands — and, the one that matters for a match, the whole realm is deterministic
// across two clients.
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
        Console.WriteLine("Taxation — tax, rations, popularity, migration\n");

        TaxCollectsGold();
        ABribeNeverOverdrawsTheTreasury();
        RationsDrawDownTheLarder();
        HungerOverridesAGenerousOrder();
        RationDemandScalesWithTheOrder();
        AFedRealmDrawsNewcomers();
        UnhappinessDrivesIdlersOff();
        YourWorkingHandsOutlastTheIdlers();
        TheTwoCommandsTakeAndClamp();
        TwoClientsAgreeOnTheRealm();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // A taxed realm fills its treasury a little every realm tick, and the pile
    // only grows. Tax is set high but rations are set lavish to cancel the
    // popularity cost, so the populace stays put and keeps paying.
    static void TaxCollectsGold()
    {
        Console.WriteLine("tax turns a populace into gold:");
        var sim = Realm(out _);
        Order(sim, SetTax(1, 4));       // +2 gold a head; -3 popularity...
        sim.AddResource(1, ResourceType.Food, 5000);
        Order(sim, SetRations(1, 4));   // ...paid off by a hearty table (+3): net zero
        Seed(sim, 1, 6);

        Ticks(sim, 80);
        int early = sim.Gold(1);
        Ticks(sim, 240);
        int later = sim.Gold(1);

        Check($"the treasury filled from tax ({early} by tick 80)", early > 0);
        Check($"and it kept climbing ({early} -> {later})", later > early);
    }

    // A bribe (the lowest tax step) PAYS the people out of the treasury for
    // popularity — but an empty treasury cannot go negative; it floors at zero
    // while the goodwill still lands.
    static void ABribeNeverOverdrawsTheTreasury()
    {
        Console.WriteLine("\na bribe cannot overdraw an empty treasury:");
        var sim = Realm(out _);
        Order(sim, SetTax(1, 0));       // -2 a head: a bribe paid out
        sim.AddResource(1, ResourceType.Food, 5000);
        Seed(sim, 1, 6);

        Ticks(sim, 700);
        Check($"the treasury floored at zero, not below ({sim.Gold(1)})", sim.Gold(1) == 0);
        Check($"and the bribe bought goodwill ({sim.Popularity(1)} > 55)", sim.Popularity(1) > 55);
    }

    // Rations are a standing cost: a fed realm spends food from the larder every
    // realm tick. A "none" order spends nothing (and pays for it in popularity,
    // proven elsewhere).
    static void RationsDrawDownTheLarder()
    {
        Console.WriteLine("\nrations eat the larder every realm tick:");
        var full = Realm(out _);
        Order(full, SetRations(1, 3));                 // full table
        full.AddResource(1, ResourceType.Food, 500);
        Seed(full, 1, 8);
        Ticks(full, 400);
        Check($"a full table drew the larder down ({full.Stockpile(1, ResourceType.Food)} of 500)",
              full.Stockpile(1, ResourceType.Food) < 500);

        var none = Realm(out _);
        Order(none, SetRations(1, 0));                 // no rations served
        none.AddResource(1, ResourceType.Food, 500);
        Seed(none, 1, 8);
        Ticks(none, 400);
        Check($"an empty table spends nothing ({none.Stockpile(1, ResourceType.Food)} of 500)",
              none.Stockpile(1, ResourceType.Food) == 500);
    }

    // Order the finest table you like — if the larder is bare the people still go
    // hungry, and hunger is the harshest popularity hit there is. The generous
    // order does NOT win goodwill it cannot feed.
    static void HungerOverridesAGenerousOrder()
    {
        Console.WriteLine("\nan order you cannot feed still starves the realm:");
        var sim = Realm(out _);
        Order(sim, SetRations(1, 6));   // a Feast on paper...
        Seed(sim, 1, 6);                // ...but not a scrap of food banked
        Ticks(sim, 700);               // approval settles every 30s now, so give it a beat
        Check($"popularity fell despite the lavish order ({sim.Popularity(1)} < 55)",
              sim.Popularity(1) < 55);
    }

    // RationDemand is the food one realm tick will draw at the current order — the
    // number the sim spends AND the HUD reads to warn "STARVING". One formula
    // (peasants x level / 6), so the two can never disagree: None nothing, a Full
    // table (level 3) half a loaf a head, a Feast (level 6) a whole loaf. Twelve
    // heads make the fractions land clean.
    static void RationDemandScalesWithTheOrder()
    {
        Console.WriteLine("\nration demand scales with the order:");
        var sim = Realm(out _);
        Seed(sim, 1, 12);
        Order(sim, SetRations(1, 0)); Check($"None asks for nothing ({sim.RationDemand(1)})", sim.RationDemand(1) == 0);
        Order(sim, SetRations(1, 3)); Check($"a full table asks half a loaf each ({sim.RationDemand(1)})", sim.RationDemand(1) == 6);
        Order(sim, SetRations(1, 6)); Check($"a Feast asks a whole loaf each ({sim.RationDemand(1)})", sim.RationDemand(1) == 12);
    }

    // The point of the whole loop: keep people fed and fairly taxed and popularity
    // climbs, and a popular realm draws newcomers up to its housing.
    static void AFedRealmDrawsNewcomers()
    {
        Console.WriteLine("\na fed, content realm grows:");
        var sim = Realm(out _);
        sim.AddResource(1, ResourceType.Food, 5000);
        Order(sim, SetRations(1, 4));   // a hearty table (+3), on top of the default light tax
        Seed(sim, 1, 2);                // room under the keep's roof to grow into

        int before = Peasants(sim, 1);
        Ticks(sim, 1200);
        Check($"popularity rose on a full larder ({sim.Popularity(1)} > 55)", sim.Popularity(1) > 55);
        Check($"and newcomers arrived ({before} -> {Peasants(sim, 1)})", Peasants(sim, 1) > before);
    }

    // Discontent empties the camp — but only of its idlers, who simply wander off.
    static void UnhappinessDrivesIdlersOff()
    {
        Console.WriteLine("\nan unhappy camp loses its idlers:");
        var sim = Realm(out _);
        Order(sim, SetTax(1, 6));       // ruinous tax, no food to ration: popularity collapses
        Seed(sim, 1, 6);

        int before = Peasants(sim, 1);
        Ticks(sim, 3000);              // slower approval settling means it stays content a while, THEN collapses — run long enough for the decline to win
        Check($"popularity collapsed ({sim.Popularity(1)})", sim.Popularity(1) < 20);
        Check($"and idlers drifted off ({before} -> {Peasants(sim, 1)})", Peasants(sim, 1) < before);
    }

    // The line the economy tests lean on: a peasant actually working a building is
    // your core labour and never emigrates, however sour the realm turns. Only
    // idle hands leave — so a lone worker with no larder still holds its post.
    static void YourWorkingHandsOutlastTheIdlers()
    {
        Console.WriteLine("\nworkers hold their post while idlers leave:");
        var sim = Realm(out _);
        Order(sim, SetTax(1, 6));                       // collapse popularity, no food
        sim.SpawnNode(ResourceType.Wood, 12, 12, 500);
        var hut = sim.PlaceBuilding(BuildingType.WoodcutterHut, 1, 10, 10);
        Seed(sim, 1, 5);                                // one will man the hut; four idle

        Ticks(sim, 20);                                 // let the hut hire before the realm bites
        Check("the hut took a worker on", hut.WorkerId != 0);
        int idlersBefore = sim.IdlePeasantCount(1);

        Ticks(sim, 2500);                               // approval settles slowly now — run long enough for the collapse to drive idlers off
        Check($"idlers left ({idlersBefore} -> {sim.IdlePeasantCount(1)})",
              sim.IdlePeasantCount(1) < idlersBefore);
        Check("but the hut kept its worker through the collapse", hut.WorkerId != 0);
    }

    // The two knobs are just commands, and they take on the tick they are issued —
    // and clamp to the legal range, so a fat-fingered value can't wedge the realm.
    static void TheTwoCommandsTakeAndClamp()
    {
        Console.WriteLine("\nSetTax / SetRations take and clamp:");
        var sim = Realm(out _);

        Order(sim, SetTax(1, 5));
        Order(sim, SetRations(1, 1));
        Check($"the tax order took ({sim.TaxLevel(1)})", sim.TaxLevel(1) == 5);
        Check($"the ration order took ({sim.RationLevel(1)})", sim.RationLevel(1) == 1);

        Order(sim, SetTax(1, 999));
        Order(sim, SetRations(1, -7));
        Check($"a wild tax clamps to the top step ({sim.TaxLevel(1)})", sim.TaxLevel(1) == Simulation.TaxSteps - 1);
        Check($"a wild ration clamps to none ({sim.RationLevel(1)})", sim.RationLevel(1) == 0);
    }

    // The one that matters for a match: two clients running the same realm — the
    // same tax and ration orders on the wire — must agree on gold, popularity, and
    // head-count on every single tick.
    static void TwoClientsAgreeOnTheRealm()
    {
        Console.WriteLine("\ntwo clients agree on the realm:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(64));
        var b = new Client(2, net, TileMap.Open(64));
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
            c.Sim.AddResource(1, ResourceType.Food, 400);
            for (int i = 0; i < 4; i++) c.Sim.SpawnPeasant(1);
        }
        // Both issue the same orders at tick 0; lockstep carries them identically.
        a.Issue(SetTax(1, 3));
        a.Issue(SetRations(1, 3));

        int desyncs = 0, first = -1;
        for (int t = 0; t < 800; t++)
        {
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 800 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check($"and the realm actually ran (gold {a.Sim.Gold(1)}, pop {a.Sim.Popularity(1)})",
              a.Sim.Gold(1) > 0);
    }

    // ---- helpers -----------------------------------------------------------

    // A bare realm: an open map with one keep for owner 1 (which seeds the realm's
    // opening tax/ration/popularity), nothing else.
    static Simulation Realm(out Simulation sim)
    {
        sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        return sim;
    }

    static Command SetTax(int owner, int step) =>
        new Command { Owner = owner, Type = CommandType.SetTax, X = step };
    static Command SetRations(int owner, int step) =>
        new Command { Owner = owner, Type = CommandType.SetRations, X = step };

    static void Order(Simulation sim, Command cmd) => sim.Tick(new[] { cmd });
    static void Ticks(Simulation sim, int n) { for (int i = 0; i < n; i++) sim.Tick(None); }
    static void Seed(Simulation sim, int owner, int n) { for (int i = 0; i < n; i++) sim.SpawnPeasant(owner); }

    static int Peasants(Simulation sim, int owner)
    {
        int n = 0;
        foreach (var u in sim.Units) if (u.IsPeasant && u.Owner == owner && u.Alive) n++;
        return n;
    }

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
        if (!ok) _failures++;
    }
}
