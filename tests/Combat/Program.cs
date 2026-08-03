// Combat — units fight, matches are won, and the dice stay in sync.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Combat — deterministic fighting, RNG in sync\n");

        MoveOnlyDrawsNoRandomness();
        AFightResolves();
        TheOutnumberedSideLoses();
        AcquiresTheNextFoeAfterAKill();
        AVeteranHardensWithKills();
        PrestigeIsEarnedInBattleAndAtCourt();
        AChampionIsMusteredForPrestige();
        GuardsHoldTheLine();
        MoveBreaksOffCombat();
        TwoClientsAgreeOnTheWholeFight();
        RngSurvivesARejoinMidFight();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // The property that keeps 0xB1A7A676 safe: a match with only Move orders
    // never touches the RNG, so the parity scenario is unaffected by combat.
    static void MoveOnlyDrawsNoRandomness()
    {
        Console.WriteLine("move-only makes no RNG draws:");
        var sim = new Simulation(TileMap.Open(48));
        uint seedState = sim.RngState;
        var u = sim.SpawnUnit(1, 5, 5);
        sim.SpawnUnit(2, 40, 40);          // an enemy exists, but nobody attacks

        Order(sim, Move(u, 20, 20));
        for (int i = 0; i < 300; i++) sim.Tick(Array.Empty<Command>());

        Check("the RNG never advanced", sim.RngState == seedState);
        Check("no unit lost health", All(sim, x => x.Hp == x.MaxHp));
    }

    static void AFightResolves()
    {
        Console.WriteLine("\na 1v1 fight ends with one unit dead:");
        var sim = new Simulation(TileMap.Open(48));
        var a = sim.SpawnUnit(1, 20, 20);
        var b = sim.SpawnUnit(2, 21, 20);          // adjacent: in melee range at once

        Order(sim, Atk(a, b));
        Order(sim, Atk(b, a));

        int killedAtTick = -1;
        for (int i = 0; i < 400 && sim.MatchWinner() < 0; i++)
        {
            sim.Tick(Array.Empty<Command>());
            if (killedAtTick < 0 && sim.Units.Count == 1) killedAtTick = sim.TickNumber;
        }

        Check($"one unit died (in ~{killedAtTick} ticks)", sim.Units.Count == 1);
        Check("the RNG advanced (damage was rolled)", sim.RngState != new Simulation().RngState);
        int winner = sim.MatchWinner();
        Check($"there is a single winner (owner {winner})", winner == 1 || winner == 2);
        Check("the surviving unit belongs to the winner",
              sim.Units.Count == 1 && sim.Units[0].Owner == winner);
    }

    // A 2v1 removes the RNG's say in WHO wins: the lone unit cannot survive two
    // attackers, so the result is asserted exactly.
    static void TheOutnumberedSideLoses()
    {
        Console.WriteLine("\ntwo against one — the one loses:");
        var sim = new Simulation(TileMap.Open(48));
        var a1 = sim.SpawnUnit(1, 20, 20);
        var a2 = sim.SpawnUnit(1, 20, 21);
        var lone = sim.SpawnUnit(2, 21, 20);

        Order(sim, Atk(new[] { a1, a2 }, lone));
        Order(sim, Atk(lone, a1));

        for (int i = 0; i < 400 && sim.MatchWinner() < 0; i++) sim.Tick(Array.Empty<Command>());

        Check("player 1 wins", sim.MatchWinner() == 1);
        Check("the lone unit is gone", sim.Units.Find(u => u.Id == lone.Id) == null);
        Check("both attackers survive", sim.Units.Count == 2);
        Check("and at least one attacker is scratched (it fought back)",
              sim.Units.Find(u => u.Id == a1.Id).Hp < 100);
    }

    static void AcquiresTheNextFoeAfterAKill()
    {
        Console.WriteLine("\none unit clears two foes, acquiring the next:");
        var sim = new Simulation(TileMap.Open(48));
        var hero = sim.SpawnUnit(1, 20, 20);
        var foe1 = sim.SpawnUnit(2, 21, 20);
        sim.SpawnUnit(2, 20, 21);                  // foe2, also adjacent, within aggro

        // Hero attacks only foe1; after it dies, the aggro scan should pick up
        // foe2 with no new order. The foes do not fight back, isolating the
        // acquisition behaviour from who-would-win.
        Order(sim, Atk(hero, foe1));

        for (int i = 0; i < 600 && sim.MatchWinner() < 0; i++) sim.Tick(Array.Empty<Command>());

        Check("player 1 wins after clearing both", sim.MatchWinner() == 1);
        Check("both enemies are dead", sim.Units.Count == 1 && sim.Units[0].Id == hero.Id);
    }

    static void MoveBreaksOffCombat()
    {
        Console.WriteLine("\na move order breaks off the fight:");
        var sim = new Simulation(TileMap.Open(48));
        var a = sim.SpawnUnit(1, 20, 20);
        var b = sim.SpawnUnit(2, 21, 20);

        Order(sim, Atk(a, b));
        for (int i = 0; i < 30; i++) sim.Tick(Array.Empty<Command>());
        Check("the fight is underway (enemy is hurt)", b.Hp < 100);
        Check("attacker has a target", a.TargetId == b.Id);

        int enemyHp = b.Hp;
        Order(sim, Move(a, 5, 5));                  // walk away
        Check("the move clears the target", a.TargetId == 0);

        for (int i = 0; i < 200 && a.HasPath; i++) sim.Tick(Array.Empty<Command>());
        Check("the attacker walked off to its destination",
              Fixed.ToInt(a.X) == 5 && Fixed.ToInt(a.Y) == 5);
        Check("and stopped dealing damage once it left", b.Hp == enemyHp);
    }

    // The one that matters most: two independent clients, the same attack orders,
    // rolling the same damage in the same order, agreeing on StateChecksum every
    // tick and on the winner.
    static void TwoClientsAgreeOnTheWholeFight()
    {
        Console.WriteLine("\ntwo clients fight the identical battle:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.SpawnUnit(1, 20, 20);            // ids 1,2 = player 1
            c.Sim.SpawnUnit(1, 20, 22);
            c.Sim.SpawnUnit(2, 22, 20);            // ids 3,4 = player 2
            c.Sim.SpawnUnit(2, 22, 22);
        }

        // Both players commit their armies on tick 1. Client.Issue stamps the
        // owner, so the raw (unit, target) ids here need no owner.
        var script = new Dictionary<int, Action>
        {
            [1] = () =>
            {
                a.Issue(AtkIds(1, 3)); b.Issue(AtkIds(1, 3));
                a.Issue(AtkIds(2, 4)); b.Issue(AtkIds(2, 4));
                a.Issue(AtkIds(3, 1)); b.Issue(AtkIds(3, 1));
                a.Issue(AtkIds(4, 2)); b.Issue(AtkIds(4, 2));
            },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 500; t++)
        {
            if (script.TryGetValue(t, out var act)) act();
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum())
            {
                if (first < 0) first = t;
                desyncs++;
            }
        }

        Check($"StateChecksum identical on all 500 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both clients agree on the RNG position", a.Sim.RngState == b.Sim.RngState);
        Check("both clients agree on the winner", a.Sim.MatchWinner() == b.Sim.MatchWinner());
        Check("both clients agree on who is left alive", a.Sim.Units.Count == b.Sim.Units.Count);
    }

    // Snapshot a fight in progress into a fresh sim and let both play it out: if
    // the RNG state did not travel exactly, the two would roll different damage
    // from the join onward and diverge.
    static void RngSurvivesARejoinMidFight()
    {
        Console.WriteLine("\na mid-fight rejoin keeps the dice in sync:");
        var host = new Simulation(TileMap.Open(48));
        var h1 = host.SpawnUnit(1, 20, 20);
        var h2 = host.SpawnUnit(1, 20, 22);
        var e1 = host.SpawnUnit(2, 22, 20);
        var e2 = host.SpawnUnit(2, 22, 22);
        Order(host, Atk(h1, e1));
        Order(host, Atk(h2, e2));
        Order(host, Atk(e1, h1));
        Order(host, Atk(e2, h2));

        for (int i = 0; i < 60; i++) host.Tick(Array.Empty<Command>());
        Check("fight is underway with the RNG advanced",
              host.RngState != new Simulation().RngState && host.Units.Count == 4);

        // A rejoiner rebuilds from a full snapshot (units, paths, combat, RNG).
        var rejoiner = new Simulation(TileMap.Open(48));
        var units = new List<Unit>();
        foreach (var u in host.Units) units.Add(u.Clone());
        rejoiner.Restore(host.TickNumber, host.NextUnitId, host.RngState, units,
                         host.NextNodeId, host.NodeList, host.Stockpiles, host.DropOffs,
                         host.NextBuildingId, host.BuildingList, host.Designs);

        Check("the rebuilt sim hashes identically at the join",
              rejoiner.StateChecksum() == host.StateChecksum());

        int desyncs = 0;
        for (int i = 0; i < 400; i++)
        {
            host.Tick(Array.Empty<Command>());
            rejoiner.Tick(Array.Empty<Command>());
            if (host.StateChecksum() != rejoiner.StateChecksum()) desyncs++;
        }
        Check("no divergence over 400 ticks after the rejoin", desyncs == 0);
        Check("same winner on both", host.MatchWinner() == rejoiner.MatchWinner());
    }

    // ---- helpers -----------------------------------------------------------
    // Owner is read from the Unit, so an order can never be aimed at the wrong
    // player by a bookkeeping slip.

    // Veterancy: a unit that slays foes hardens — it ranks up, growing tougher (a
    // bigger max hp) and hitting harder. Fed six hapless dummies to fell in a row.
    static void AVeteranHardensWithKills()
    {
        Console.WriteLine("\na unit that slays foes hardens into a veteran:");
        var sim = new Simulation(TileMap.Open(24));
        int dummy = sim.RegisterDesign(new UnitDesign { Hp = 5, Damage = 0, SpeedStat = 5, RangeStat = 3, Cooldown = 10 });   // harmless, one-shot fodder
        var hero = sim.SpawnUnit(1, 8, 8);                 // a plain soldier (design 0)
        int baseMax = hero.MaxHp;
        Check("it starts a Regular", sim.RankOf(hero) == 0);

        var foes = new List<Unit>();
        foreach (var off in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1) })
            foes.Add(sim.SpawnUnit(2, 8 + off.Item1, 8 + off.Item2, dummy));
        Order(sim, Atk(hero, foes[0]));                    // strike the first; it acquires the rest as they fall
        for (int i = 0; i < 3000 && sim.Units.Count > 1; i++) sim.Tick(Array.Empty<Command>());

        Check($"it racked up kills ({hero.Kills})", hero.Kills >= 5);
        Check("and rose to Elite", sim.RankOf(hero) == 2);
        Check($"growing tougher ({hero.MaxHp} > {baseMax})", hero.MaxHp > baseMax);
        Check("unharmed by the harmless fodder", hero.Hp > baseMax);   // healed past its old max by the promotions
    }

    // Prestige is earned three ways: felling foes, the Royal Kitchen's feasts, and a
    // standing Statue's slow compounding. All deterministic, all riding the stock array.
    static void PrestigeIsEarnedInBattleAndAtCourt()
    {
        Console.WriteLine("\nprestige earned in battle & at court:");

        // Battle glory — a soldier that slays harmless fodder earns its court renown.
        var war = new Simulation(TileMap.Open(24));
        int dummy = war.RegisterDesign(new UnitDesign { Hp = 5, Damage = 0, SpeedStat = 5, RangeStat = 3, Cooldown = 10 });
        var hero = war.SpawnUnit(1, 8, 8, 0);
        var foes = new List<Unit>();
        foreach (var off in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            foes.Add(war.SpawnUnit(2, 8 + off.Item1, 8 + off.Item2, dummy));
        Order(war, Atk(hero, foes[0]));
        for (int i = 0; i < 2000 && war.Units.Count > 1; i++) war.Tick(Array.Empty<Command>());
        Check($"felling foes earned the court prestige ({war.Prestige(1)})", war.Prestige(1) > 0);

        // The Royal Kitchen — feasts turn food into renown (and a little goodwill).
        var court = new Simulation(TileMap.Open(48));
        court.PlaceBuilding(BuildingType.RoyalKitchen, 1, 20, 20);
        court.AddResource(1, ResourceType.Food, 400);
        int food0 = court.Stockpiles[1][(int)ResourceType.Food];
        for (int i = 0; i < 300; i++) court.Tick(Array.Empty<Command>());
        Check($"the Royal Kitchen's feasts earned prestige ({court.Prestige(1)})", court.Prestige(1) > 0);
        Check("and ate food to lay them on", court.Stockpiles[1][(int)ResourceType.Food] < food0);

        // The Statue — the compounding monument: it just stands there and radiates renown.
        var monument = new Simulation(TileMap.Open(48));
        monument.PlaceBuilding(BuildingType.Statue, 1, 20, 20);
        for (int i = 0; i < 300; i++) monument.Tick(Array.Empty<Command>());
        Check($"a standing Statue radiates prestige ({monument.Prestige(1)})", monument.Prestige(1) > 0);
    }

    // A Champion is mustered for Prestige at a Royal Kitchen — never trained, never
    // point-bought — and it is a mighty unit.
    static void AChampionIsMusteredForPrestige()
    {
        Console.WriteLine("\na champion is mustered for prestige:");
        var sim = new Simulation(TileMap.Open(48));
        foreach (var d in Skirmish.Designs()) sim.RegisterDesign(d);   // registers the Champion (design 9)
        sim.PlaceBuilding(BuildingType.RoyalKitchen, 1, 20, 20);

        Check("cannot muster with no prestige", !sim.CanMusterChampion(1));

        sim.AddResource(1, ResourceType.Food, 4000);   // feast up the renown to afford one
        for (int i = 0; i < 6000 && sim.Prestige(1) < Simulation.ChampionCost; i++) sim.Tick(Array.Empty<Command>());
        Check($"the court amassed enough prestige ({sim.Prestige(1)})", sim.Prestige(1) >= Simulation.ChampionCost);
        Check("and now it can muster", sim.CanMusterChampion(1));

        int units = sim.Units.Count, p = sim.Prestige(1);
        Order(sim, new Command { Owner = 1, Seq = 1, Type = CommandType.MusterChampion });
        Check("a champion appeared", sim.Units.Count == units + 1);
        Check($"prestige was spent ({p} → {sim.Prestige(1)})", sim.Prestige(1) == p - Simulation.ChampionCost);
        var champ = sim.Units[sim.Units.Count - 1];
        Check($"and it's a mighty unit ({champ.MaxHp} hp)", champ.MaxHp >= 400);
    }

    // A guard intercepts an enemy that enters its territory, warns the realm, and —
    // crucially — does NOT chase a foe beyond its own land: it holds the line.
    static void GuardsHoldTheLine()
    {
        Console.WriteLine("\nguards hold the territory line:");

        var sim = new Simulation(TileMap.Open(64));
        sim.FogEnabled = false;
        sim.PlaceBuilding(BuildingType.Keep, 1, 30, 30);
        var rect = sim.HomeRect(1).Value;
        var guard = sim.SpawnUnit(1, 32, 32, 0);
        Order(sim, new Command { Owner = 1, Seq = 1, Type = CommandType.SetGuard, UnitIds = new[] { guard.Id }, X = 1 });
        Check("a guard on clear land holds no target", guard.TargetId == 0);

        // An enemy steps onto the realm's land.
        int ix = rect.minX + 3, iy = rect.minY + 3;
        var foe = sim.SpawnUnit(2, ix, iy, 0);
        Check("the intruder is inside the territory", ix <= rect.maxX && iy <= rect.maxY);
        for (int i = 0; i < 15; i++) sim.Tick(Array.Empty<Command>());
        Check("the guard locks onto the intruder", guard.TargetId == foe.Id);
        bool alerted = false;
        foreach (var s in sim.ScoutSightings) if (s.Owner == 1 && s.Kind == SightingKind.Intruder) alerted = true;
        Check("and the realm is warned of the incursion", alerted);

        // A separate realm: its guard must ignore an enemy well OUTSIDE the border.
        var far = new Simulation(TileMap.Open(64));
        far.FogEnabled = false;
        far.PlaceBuilding(BuildingType.Keep, 1, 12, 12);
        var g2 = far.SpawnUnit(1, 13, 13, 0);
        Order(far, new Command { Owner = 1, Seq = 1, Type = CommandType.SetGuard, UnitIds = new[] { g2.Id }, X = 1 });
        far.SpawnUnit(2, 60, 60, 0);   // far past the home rect
        for (int i = 0; i < 30; i++) far.Tick(Array.Empty<Command>());
        Check("a guard ignores enemies beyond its territory (holds the line)", g2.TargetId == 0);
    }

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Move(Unit u, int x, int y) => new Command
    {
        Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y,
    };

    static Command Atk(Unit u, Unit target) => new Command
    {
        Owner = u.Owner, Type = CommandType.Attack, UnitIds = new[] { u.Id }, TargetId = target.Id,
    };

    static Command Atk(Unit[] us, Unit target)
    {
        var ids = new int[us.Length];
        for (int i = 0; i < us.Length; i++) ids[i] = us[i].Id;
        return new Command { Owner = us[0].Owner, Type = CommandType.Attack, UnitIds = ids, TargetId = target.Id };
    }

    // For the loopback test, where Client.Issue stamps the owner itself.
    static Command AtkIds(int unit, int target) => new Command
    {
        Type = CommandType.Attack, UnitIds = new[] { unit }, TargetId = target,
    };

    static bool All(Simulation sim, Func<Unit, bool> pred)
    {
        foreach (var u in sim.Units) if (!pred(u)) return false;
        return true;
    }

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
