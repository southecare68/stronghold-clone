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
