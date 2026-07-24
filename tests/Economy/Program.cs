// Economy — workers gather, haul, and deposit, deterministically and in sync.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("Economy — gathering loop, deterministic and in sync\n");

        AWorkerGathersAndDeposits();
        ANodeDepletesAndTheWorkerStopsMoveOnlyNoEconomy();
        MoveBreaksOffGathering();
        NeedsADropOff();
        TwoClientsAgreeOnTheEconomy();
        EconomySurvivesARejoin();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    static void AWorkerGathersAndDeposits()
    {
        Console.WriteLine("a worker gathers a load and banks it:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 10, 10);
        var worker = sim.SpawnUnit(1, 10, 10);
        var node = sim.SpawnNode(ResourceType.Wood, 16, 10, 100);

        Order(sim, Gather(worker, node));

        Check("nothing banked at the start", sim.Stockpile(1, ResourceType.Wood) == 0);

        // Run long enough for at least a couple of round trips.
        int nodeStart = node.Amount;
        for (int i = 0; i < 600; i++) sim.Tick(Array.Empty<Command>());

        int banked = sim.Stockpile(1, ResourceType.Wood);
        Check($"wood was banked ({banked})", banked > 0);
        Check("it is wood, not another resource",
              sim.Stockpile(1, ResourceType.Stone) == 0 && sim.Stockpile(1, ResourceType.Food) == 0);

        // Conservation: what left the node equals banked + whatever is still being
        // carried. Nothing is created or lost.
        int carried = worker.CarryAmount;
        int removedFromNode = nodeStart - node.Amount;
        Check($"conserved: node lost {removedFromNode} = banked {banked} + carried {carried}",
              removedFromNode == banked + carried);
        Check("the worker is back on its feet, not stuck", worker.Alive);
    }

    static void ANodeDepletesAndTheWorkerStopsMoveOnlyNoEconomy()
    {
        Console.WriteLine("\na small node is emptied and the worker stands down:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 10, 10);
        var worker = sim.SpawnUnit(1, 10, 10);
        var node = sim.SpawnNode(ResourceType.Stone, 14, 10, 15);   // less than one load and a bit

        Order(sim, Gather(worker, node));
        for (int i = 0; i < 2000 && (sim.Nodes.Count > 0 || worker.CarryAmount > 0); i++)
            sim.Tick(Array.Empty<Command>());

        Check("the node is gone", sim.Nodes.Count == 0);
        Check("every last unit of it was banked (15)", sim.Stockpile(1, ResourceType.Stone) == 15);
        Check("the worker carries nothing and has no job",
              worker.CarryAmount == 0 && worker.Job == Job.None);
    }

    static void MoveBreaksOffGathering()
    {
        Console.WriteLine("\na move order calls the worker off the job:");
        var sim = new Simulation(TileMap.Open(48));
        sim.SetDropOff(1, 10, 10);
        var worker = sim.SpawnUnit(1, 10, 10);
        var node = sim.SpawnNode(ResourceType.Food, 16, 10, 100);

        Order(sim, Gather(worker, node));
        for (int i = 0; i < 40; i++) sim.Tick(Array.Empty<Command>());
        Check("the worker took the job", worker.Job == Job.Gathering);

        Order(sim, Move(worker, 30, 30));
        Check("the move ends the job", worker.Job == Job.None);

        int nodeBefore = node.Amount;
        for (int i = 0; i < 300 && worker.HasPath; i++) sim.Tick(Array.Empty<Command>());
        Check("the worker walked to the move target",
              Fixed.ToInt(worker.X) == 30 && Fixed.ToInt(worker.Y) == 30);
        Check("and stopped drawing down the node", node.Amount == nodeBefore);
    }

    static void NeedsADropOff()
    {
        Console.WriteLine("\nno drop-off, no job:");
        var sim = new Simulation(TileMap.Open(48));
        // deliberately NO SetDropOff for owner 1
        var worker = sim.SpawnUnit(1, 10, 10);
        var node = sim.SpawnNode(ResourceType.Wood, 16, 10, 100);

        Order(sim, Gather(worker, node));
        Check("the gather order is refused with nowhere to deposit", worker.Job == Job.None);
    }

    static void TwoClientsAgreeOnTheEconomy()
    {
        Console.WriteLine("\ntwo clients run the identical economy:");
        var net = new LoopbackTransport();
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);

        // Identical setup on both: drop-offs, workers, nodes.
        foreach (var c in new[] { a, b })
        {
            c.Sim.SetDropOff(1, 6, 6);
            c.Sim.SetDropOff(2, 40, 40);
            c.Sim.SpawnUnit(1, 6, 6);    // id 1, worker for player 1
            c.Sim.SpawnUnit(1, 7, 6);    // id 2
            c.Sim.SpawnUnit(2, 40, 40);  // id 3, worker for player 2
            c.Sim.SpawnNode(ResourceType.Wood, 12, 6, 200);   // node id 1
            c.Sim.SpawnNode(ResourceType.Stone, 40, 34, 200); // node id 2
        }

        var script = new Dictionary<int, Action>
        {
            [1] = () =>
            {
                a.Issue(GatherIds(1, 1)); b.Issue(GatherIds(1, 1));
                a.Issue(GatherIds(2, 1)); b.Issue(GatherIds(2, 1));
                a.Issue(GatherIds(3, 2)); b.Issue(GatherIds(3, 2));
            },
        };

        int desyncs = 0, first = -1;
        for (int t = 0; t < 800; t++)
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

        Check($"StateChecksum identical on all 800 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check("both banked the same wood for player 1",
              a.Sim.Stockpile(1, ResourceType.Wood) == b.Sim.Stockpile(1, ResourceType.Wood) &&
              a.Sim.Stockpile(1, ResourceType.Wood) > 0);
        Check("both banked the same stone for player 2",
              a.Sim.Stockpile(2, ResourceType.Stone) == b.Sim.Stockpile(2, ResourceType.Stone) &&
              a.Sim.Stockpile(2, ResourceType.Stone) > 0);
    }

    static void EconomySurvivesARejoin()
    {
        Console.WriteLine("\na rejoin rebuilds the whole economy:");
        var host = new Simulation(TileMap.Open(48));
        host.SetDropOff(1, 6, 6);
        var w1 = host.SpawnUnit(1, 6, 6);
        var w2 = host.SpawnUnit(1, 7, 6);
        var node = host.SpawnNode(ResourceType.Wood, 14, 6, 300);
        Order(host, Gather(w1, node));
        Order(host, Gather(w2, node));

        // Run partway: some banked, workers carrying, node drawn down.
        for (int i = 0; i < 250; i++) host.Tick(Array.Empty<Command>());
        Check("economy underway (banked something, node drawn down)",
              host.Stockpile(1, ResourceType.Wood) > 0 && node.Amount < 300);

        // Rebuild from a full snapshot.
        var rejoiner = new Simulation(TileMap.Open(48));
        var units = new List<Unit>();
        foreach (var u in host.Units) units.Add(u.Clone());
        rejoiner.Restore(host.TickNumber, host.NextUnitId, host.RngState, units,
                         host.NextNodeId, host.NodeList, host.Stockpiles, host.DropOffs,
                         host.NextBuildingId, host.BuildingList, host.Designs);

        Check("the rebuilt sim hashes identically at the join",
              rejoiner.StateChecksum() == host.StateChecksum());
        Check("the stockpile came across",
              rejoiner.Stockpile(1, ResourceType.Wood) == host.Stockpile(1, ResourceType.Wood));
        Check("the node and its remaining amount came across",
              rejoiner.Nodes.Count == 1 && rejoiner.Nodes[0].Amount == node.Amount);

        int desyncs = 0;
        for (int i = 0; i < 500; i++)
        {
            host.Tick(Array.Empty<Command>());
            rejoiner.Tick(Array.Empty<Command>());
            if (host.StateChecksum() != rejoiner.StateChecksum()) desyncs++;
        }
        Check("no divergence over 500 ticks after the rejoin", desyncs == 0);
        Check("both banked the same total in the end",
              host.Stockpile(1, ResourceType.Wood) == rejoiner.Stockpile(1, ResourceType.Wood));
    }

    // ---- helpers -----------------------------------------------------------

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Move(Unit u, int x, int y) => new Command
    {
        Owner = u.Owner, Type = CommandType.Move, UnitIds = new[] { u.Id }, X = x, Y = y,
    };

    static Command Gather(Unit u, ResourceNode node) => new Command
    {
        Owner = u.Owner, Type = CommandType.Gather, UnitIds = new[] { u.Id }, TargetId = node.Id,
    };

    // For the loopback test, where Client.Issue stamps the owner.
    static Command GatherIds(int unit, int nodeId) => new Command
    {
        Type = CommandType.Gather, UnitIds = new[] { unit }, TargetId = nodeId,
    };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
