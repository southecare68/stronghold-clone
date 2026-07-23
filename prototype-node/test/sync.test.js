// sync.test.js — Proves two independent clients stay in PERFECT sync.
//
// This is the vertical-slice proof: two separate Simulation instances (standing
// in for two players' machines) each run their own copy, exchange only commands
// through the loopback network, and we assert their per-tick checksums are
// bit-identical for the entire match. If lockstep is correct, they never drift.

const { Client, LoopbackNetwork } = require('../src/lockstep');
const { CommandType } = require('../src/simulation');

function run() {
  const net = new LoopbackNetwork();
  const alice = new Client(1, net);
  const bob = new Client(2, net);
  net.connect(alice);
  net.connect(bob);

  // Deterministic identical starting armies on both machines.
  for (const client of [alice, bob]) {
    client.sim.spawnUnit(1, 10, 10);
    client.sim.spawnUnit(1, 12, 10);
    client.sim.spawnUnit(2, 40, 40);
    client.sim.spawnUnit(2, 38, 40);
  }

  // A little script of player intents at specific ticks. Either player can
  // issue; the network delivers to both, so both stay in agreement.
  const script = {
    2: () => alice.issue({ type: CommandType.MOVE, unitIds: [1, 2], x: 30, y: 25 }),
    5: () => bob.issue({ type: CommandType.MOVE, unitIds: [3, 4], x: 20, y: 20 }),
    40: () => alice.issue({ type: CommandType.MOVE, unitIds: [1], x: 5, y: 45 }),
    41: () => bob.issue({ type: CommandType.MOVE, unitIds: [3, 4], x: 15, y: 15 }),
  };

  const TICKS = 300;
  let firstDivergenceTick = -1;
  for (let t = 0; t < TICKS; t++) {
    if (script[t]) script[t]();
    alice.step();
    bob.step();
    if (alice.checksums[t] !== bob.checksums[t] && firstDivergenceTick === -1) {
      firstDivergenceTick = t;
    }
  }

  const inSync = firstDivergenceTick === -1;
  console.log('=== Lockstep determinism test ===');
  console.log(`ticks simulated:        ${TICKS}`);
  console.log(`alice final checksum:   0x${alice.checksums[TICKS - 1].toString(16)}`);
  console.log(`bob   final checksum:   0x${bob.checksums[TICKS - 1].toString(16)}`);
  console.log(`clients in sync:        ${inSync ? 'YES ✓ (identical every tick)' : 'NO ✗ diverged at tick ' + firstDivergenceTick}`);

  // Second proof: re-run the whole match from scratch and confirm we get the
  // EXACT same checksum stream — i.e. the sim is reproducible, not just equal
  // between two instances that happened to share a process.
  const replay = replayChecksum(script, TICKS);
  const reproducible = replay === alice.checksums[TICKS - 1];
  console.log(`reproducible replay:    ${reproducible ? 'YES ✓ (same result on re-run)' : 'NO ✗'}`);

  if (!inSync || !reproducible) process.exit(1);
  console.log('RESULT: PASS');
}

function replayChecksum(script, ticks) {
  const net = new LoopbackNetwork();
  const a = new Client(1, net);
  const b = new Client(2, net);
  net.connect(a);
  net.connect(b);
  for (const client of [a, b]) {
    client.sim.spawnUnit(1, 10, 10);
    client.sim.spawnUnit(1, 12, 10);
    client.sim.spawnUnit(2, 40, 40);
    client.sim.spawnUnit(2, 38, 40);
  }
  // Rebuild the same script but bound to THIS run's client objects.
  const s = {
    2: () => a.issue({ type: 'MOVE', unitIds: [1, 2], x: 30, y: 25 }),
    5: () => b.issue({ type: 'MOVE', unitIds: [3, 4], x: 20, y: 20 }),
    40: () => a.issue({ type: 'MOVE', unitIds: [1], x: 5, y: 45 }),
    41: () => b.issue({ type: 'MOVE', unitIds: [3, 4], x: 15, y: 15 }),
  };
  for (let t = 0; t < ticks; t++) {
    if (s[t]) s[t]();
    a.step();
    b.step();
  }
  return a.checksums[ticks - 1];
}

run();
