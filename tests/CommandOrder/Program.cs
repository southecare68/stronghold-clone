// CommandOrder — proves the command ordering is TOTAL, so peers can't diverge.
//
// The bug this guards against: if CanonicalOrder ever returns 0 for two distinct
// commands, the sort leaves them in whatever order the caller supplied, and a
// tick's outcome starts depending on assembly order rather than on the commands
// themselves. Conflicting commands (same unit, two destinations, same tick) make
// that visible: whichever is applied LAST wins.
//
// NOTE ON WHAT CHANGED. When commands were broadcast individually, a peer's
// arrival order was the thing that varied and this test drove it through a
// transport. Turn-based lockstep closed that path — a player's commands now
// travel together in one turn, and turns are gathered in owner order — so the
// transport can no longer scramble them. Both layers are still exercised below,
// but the load-bearing check is now the direct one against Simulation.Tick:
// the sim's contract is that a tick's result depends on its command SET, never
// on the order the set was handed over. That is what a total order buys, and it
// is what keeps the guarantee alive if the gathering code is ever rewritten.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    static void Main()
    {
        Console.WriteLine("CommandOrder — canonical ordering must be total\n");

        TickIsIndependentOfCommandOrder();
        ConflictingOrdersResolveByIssueOrder();
        TurnsArrivingReversed();
        DifferentOwnersStillOrderByOwner();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // The core claim. Same commands, six different hand-over orders, one result.
    static void TickIsIndependentOfCommandOrder()
    {
        Console.WriteLine("Simulation.Tick depends on the command SET, not its order:");

        var cmds = new[]
        {
            Cmd(owner: 1, seq: 1, unit: 1, x: 30, y: 30),
            Cmd(owner: 1, seq: 2, unit: 1, x: 5,  y: 40),   // conflicts with the first
            Cmd(owner: 2, seq: 1, unit: 4, x: 20, y: 20),
        };

        uint reference = RunWith(cmds);
        int checkedOrders = 0;
        foreach (var order in Permutations(cmds))
        {
            checkedOrders++;
            if (RunWith(order) == reference) continue;
            Check($"permutation {checkedOrders} matches the reference result", false);
            return;
        }
        Check($"all {checkedOrders} orderings produce checksum 0x{reference:X8}", true);
    }

    // A total order is not enough on its own — it has to be the RIGHT order.
    // A player's own commands must resolve in the order that player issued them.
    static void ConflictingOrdersResolveByIssueOrder()
    {
        Console.WriteLine("\nconflicting orders from one player:");
        var sim = Army();
        sim.Tick(new List<Command>
        {
            Cmd(owner: 1, seq: 2, unit: 1, x: 5,  y: 40),   // handed over first...
            Cmd(owner: 1, seq: 1, unit: 1, x: 30, y: 30),   // ...but issued first
        });
        var u = sim.Units.Find(v => v.Id == 1);
        Check("the later-issued order wins regardless of hand-over order (5,40)",
              u.Tx == Fixed.FromInt(5) && u.Ty == Fixed.FromInt(40));
    }

    // The transport-level version: two peers, same turns, opposite delivery order.
    static void TurnsArrivingReversed()
    {
        Console.WriteLine("\nturns delivered to the two peers in opposite orders:");
        var net = new SwapTransport();
        var (a, b) = Peers(net);

        a.Issue(Move(unit: 1, x: 30, y: 30));
        a.Issue(Move(unit: 1, x: 5, y: 40));
        b.Issue(Move(unit: 4, x: 12, y: 12));

        RunAndCompare(a, b, net, ticks: 200);

        var ua = a.Sim.Units.Find(u => u.Id == 1);
        var ub = b.Sim.Units.Find(u => u.Id == 1);
        Check($"peers agree on unit 1's target — A ({Show(ua.Tx)},{Show(ua.Ty)}) " +
              $"vs B ({Show(ub.Tx)},{Show(ub.Ty)})", ua.Tx == ub.Tx && ua.Ty == ub.Ty);
        Check("the later of the player's two orders wins (5,40)",
              ua.Tx == Fixed.FromInt(5) && ua.Ty == Fixed.FromInt(40));
    }

    // The ordering that already worked must keep working.
    static void DifferentOwnersStillOrderByOwner()
    {
        Console.WriteLine("\ncommands from different owners:");
        var net = new SwapTransport();
        var (a, b) = Peers(net);

        b.Issue(Move(unit: 4, x: 30, y: 30));   // player 2 issues first...
        a.Issue(Move(unit: 1, x: 12, y: 15));   // ...player 1 second

        RunAndCompare(a, b, net, ticks: 200);
        Check("both players' units reached their own targets",
              a.Sim.Units.Find(u => u.Id == 1).Tx == Fixed.FromInt(12) &&
              a.Sim.Units.Find(u => u.Id == 4).Tx == Fixed.FromInt(30));
    }

    // ---- helpers -----------------------------------------------------------

    static Simulation Army()
    {
        var sim = new Simulation();
        sim.SpawnUnit(1, 8, 8);
        sim.SpawnUnit(1, 11, 8);
        sim.SpawnUnit(1, 8, 11);
        sim.SpawnUnit(2, 44, 40);
        sim.SpawnUnit(2, 47, 40);
        return sim;
    }

    // Apply the commands on tick 0, then let the world settle, and hash it.
    static uint RunWith(IReadOnlyList<Command> cmds)
    {
        var sim = Army();
        sim.Tick(cmds);
        for (int i = 0; i < 200; i++) sim.Tick(Array.Empty<Command>());
        return sim.Checksum();
    }

    static IEnumerable<Command[]> Permutations(Command[] source)
    {
        var idx = new int[source.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        foreach (var p in Permute(idx, 0))
        {
            var result = new Command[p.Length];
            for (int i = 0; i < p.Length; i++) result[i] = source[p[i]];
            yield return result;
        }
    }

    static IEnumerable<int[]> Permute(int[] a, int k)
    {
        if (k == a.Length) { yield return (int[])a.Clone(); yield break; }
        for (int i = k; i < a.Length; i++)
        {
            (a[k], a[i]) = (a[i], a[k]);
            foreach (var p in Permute(a, k + 1)) yield return p;
            (a[k], a[i]) = (a[i], a[k]);
        }
    }

    static Command Cmd(int owner, int seq, int unit, int x, int y) => new Command
    {
        Owner = owner, Seq = seq, Type = CommandType.Move,
        UnitIds = new[] { unit }, X = x, Y = y,
    };

    static Command Move(int unit, int x, int y) => new Command
    {
        Type = CommandType.Move,
        UnitIds = new[] { unit },
        X = x,
        Y = y,
    };

    static (Client, Client) Peers(SwapTransport net)
    {
        var a = new Client(1, net);
        var b = new Client(2, net);
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.SpawnUnit(1, 8, 8);
            c.Sim.SpawnUnit(1, 11, 8);
            c.Sim.SpawnUnit(1, 8, 11);
            c.Sim.SpawnUnit(2, 44, 40);
            c.Sim.SpawnUnit(2, 47, 40);
        }
        return (a, b);
    }

    static void RunAndCompare(Client a, Client b, SwapTransport net, int ticks)
    {
        int desyncs = 0, first = -1, stalls = 0;
        for (int t = 0; t < ticks; t++)
        {
            a.SendInput();
            b.SendInput();
            net.Flush();
            if (!a.TryStep() || !b.TryStep()) stalls++;
            if (a.Sim.Checksum() != b.Sim.Checksum())
            {
                if (first < 0) first = t;
                desyncs++;
            }
        }
        Check($"never stalled on a loss-free transport", stalls == 0);
        Check($"in sync on all {ticks} ticks" +
              (desyncs > 0 ? $" (desynced {desyncs}x, first at tick {first})" : ""),
              desyncs == 0);
    }

    static string Show(int fixedValue) => (fixedValue / (double)Fixed.One).ToString("0.###");

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }

    // A transport that models what a network can still reorder: whole turns.
    // Client 0 receives them as sent, client 1 receives them reversed.
    sealed class SwapTransport : ITransport
    {
        readonly List<Client> _clients = new();
        readonly List<TurnInput> _pending = new();

        public void Connect(Client c) => _clients.Add(c);
        public int PlayerCount => _clients.Count;
        public void Poll() { }
        public void Send(TurnInput turn) => _pending.Add(turn);

        public void Flush()
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                var order = new List<TurnInput>(_pending);
                if (i % 2 == 1) order.Reverse();
                foreach (var t in order) _clients[i].Receive(t.Clone());
            }
            _pending.Clear();
        }
    }
}
