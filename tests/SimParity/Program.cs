// Program.cs — Port-parity test: does the C# simulation reproduce the verified
// Node prototype exactly?
//
// WHY THIS TEST EXISTS
// The Node prototype in prototype-node/ is the known-good reference: it is
// proven to hold two clients bit-identical for 300 ticks. game/Sim/ is a hand
// port of that core. A hand port is a guess until something checks it, and the
// cheapest complete check is the one this file performs — replay the EXACT
// scenario from prototype-node/test/sync.test.js and compare the 32-bit state
// checksum. A single wrong truncation, a stray double, one operand widened to
// long in the wrong place, and this number changes. It cannot pass by accident.
//
// The magic number 0xB1A7A676 is not arbitrary: it is what `node
// test/sync.test.js` prints. If you ever intentionally change the sim's
// behaviour (movement speed, spawn order, checksum layout), BOTH sides move
// together and you re-derive this constant from the Node run — never edit it
// here to make a red test go green. A changed checksum with unchanged intent is
// a desync bug that has not happened yet.
//
// Deliberately Godot-free, so it runs on any machine with `dotnet` — including
// the Ubuntu x86 box that serves as the cross-architecture partner.
//
//   dotnet run --project tests/SimParity      (exit 0 = pass, 1 = fail)

using System;
using System.Collections.Generic;
using Sim;

namespace SimParity
{
    static class Program
    {
        // The final checksum after 300 ticks of the reference scenario, taken
        // from `node prototype-node/test/sync.test.js`.
        const uint ExpectedFinalChecksum = 0xB1A7A676;

        const int Ticks = 300;

        // Sparse checkpoints from the same Node run. These do nothing when the
        // test passes; when it fails they bisect the divergence to a handful of
        // ticks, which is the difference between "the port is wrong somewhere"
        // and "the port is wrong at tick 5, right after the first MOVE lands".
        static readonly (int Tick, uint Checksum)[] Checkpoints =
        {
            (  0, 0x3C34A3F8),
            (  1, 0x87EDE9FB),
            (  5, 0x9B9C6CBE),
            ( 10, 0xB22C3711),
            ( 20, 0xDE028292),
            ( 50, 0x9314A41D),
            (100, 0xF674BB8B),
            (150, 0xBFE2D855),
            (200, 0x8EB7F3B9),
            (250, 0x3C0D8837),
            (299, 0xB1A7A676),
        };

        static int Main()
        {
            Console.WriteLine("=== C# port parity vs. Node prototype ===");

            var run = Replay();
            var rerun = Replay();

            bool ok = true;
            ok &= Check("clients in sync (identical every tick)",
                        run.FirstDivergenceTick == -1,
                        run.FirstDivergenceTick == -1
                            ? $"{Ticks} ticks, no drift"
                            : $"diverged at tick {run.FirstDivergenceTick}");

            uint final = run.Checksums[Ticks - 1];
            ok &= Check("final checksum matches Node",
                        final == ExpectedFinalChecksum,
                        $"got 0x{final:X8}, expected 0x{ExpectedFinalChecksum:X8}");

            ok &= Check("reproducible replay (same result on re-run)",
                        rerun.Checksums[Ticks - 1] == final,
                        $"re-run 0x{rerun.Checksums[Ticks - 1]:X8}");

            ok &= CheckCheckpoints(run.Checksums);

            Console.WriteLine();
            Console.WriteLine(ok
                ? "RESULT: PASS — the C# sim is a faithful port of the verified Node core."
                : "RESULT: FAIL — see the first mismatching checkpoint above, then diff the\n" +
                  "        C# movement/fixed-point math against prototype-node/src/ at that tick.");
            return ok ? 0 : 1;
        }

        // Replays prototype-node/test/sync.test.js beat for beat: same spawns in
        // the same order, same commands issued by the same player on the same
        // ticks, same tick count.
        static (uint[] Checksums, int FirstDivergenceTick) Replay()
        {
            var net = new LoopbackTransport();
            var alice = new Client(1, net);
            var bob = new Client(2, net);
            net.Connect(alice);
            net.Connect(bob);

            // Deterministic identical starting armies on both machines. Spawn
            // order fixes the unit ids (1,2 = Alice's, 3,4 = Bob's).
            foreach (var c in new[] { alice, bob })
            {
                c.Sim.SpawnUnit(1, 10, 10);
                c.Sim.SpawnUnit(1, 12, 10);
                c.Sim.SpawnUnit(2, 40, 40);
                c.Sim.SpawnUnit(2, 38, 40);
            }

            // Player intents at specific ticks. Either player may issue; the
            // transport delivers to both, so both stay in agreement.
            var script = new Dictionary<int, Action>
            {
                [2]  = () => alice.Issue(Move(new[] { 1, 2 }, 30, 25)),
                [5]  = () => bob.Issue(Move(new[] { 3, 4 }, 20, 20)),
                [40] = () => alice.Issue(Move(new[] { 1 }, 5, 45)),
                [41] = () => bob.Issue(Move(new[] { 3, 4 }, 15, 15)),
            };

            var checksums = new uint[Ticks];
            int firstDivergence = -1;

            for (int t = 0; t < Ticks; t++)
            {
                if (script.TryGetValue(t, out var issue)) issue();

                // Every client publishes its turn before any client consumes one:
                // a client will not advance until it holds every player's input,
                // so stepping alice first would just stall her.
                alice.SendInput();
                bob.SendInput();

                // Over LoopbackTransport nothing can be late, so a stall here
                // means the turn bookkeeping itself is wrong.
                if (!alice.TryStep() || !bob.TryStep())
                    throw new InvalidOperationException(
                        $"stalled at tick {t} on a loss-free transport — turn scheduling is broken");

                checksums[t] = alice.Sim.Checksum();
                if (checksums[t] != bob.Sim.Checksum() && firstDivergence == -1)
                    firstDivergence = t;
            }

            return (checksums, firstDivergence);
        }

        static Command Move(int[] unitIds, int x, int y) =>
            new Command { Type = CommandType.Move, UnitIds = unitIds, X = x, Y = y };

        static bool CheckCheckpoints(uint[] checksums)
        {
            foreach (var (tick, expected) in Checkpoints)
            {
                if (checksums[tick] != expected)
                    return Check($"checkpoint tick {tick}", false,
                                 $"got 0x{checksums[tick]:X8}, expected 0x{expected:X8} " +
                                 "— this is the earliest reported divergence");
            }
            return Check($"all {Checkpoints.Length} checkpoints match", true,
                         "tick stream tracks Node throughout, not just at the end");
        }

        static bool Check(string label, bool pass, string detail)
        {
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {label,-46} {detail}");
            return pass;
        }
    }
}
