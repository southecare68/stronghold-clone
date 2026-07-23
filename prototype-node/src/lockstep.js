// lockstep.js — The multiplayer synchronization model.
//
// The classic RTS netcode: "deterministic lockstep with input delay."
//
//   * Each player runs their OWN full copy of the Simulation.
//   * When a player issues a command, it is NOT executed immediately. It is
//     scheduled to run on a future tick (currentTick + INPUT_DELAY) and sent to
//     every player. The delay gives the command time to reach everyone over the
//     network before that tick is simulated.
//   * On each tick, every machine executes the identical set of commands for
//     that tick, so all copies stay bit-for-bit identical.
//   * We compare checksums every tick to prove they never drift apart.
//
// This file models a single client. A trivial in-memory "network" in the test
// delivers commands between two clients so we can prove sync headlessly.

const { Simulation } = require('./simulation');

const INPUT_DELAY = 3; // ticks between issuing a command and executing it

class Client {
  constructor(playerId, network) {
    this.playerId = playerId;
    this.network = network;
    this.sim = new Simulation();
    // Commands to execute, keyed by the tick they run on.
    this.scheduled = new Map(); // tickNumber -> Command[]
    this.checksums = []; // checksum recorded per executed tick
    // Stamped onto every command we issue. With playerId it gives each command a
    // unique, machine-independent identity, so canonicalCommandOrder can be a
    // TOTAL order rather than leaning on Array.sort's stability — which only
    // preserves ARRIVAL order, and arrival order differs per machine.
    this._nextSeq = 1;
  }

  // Called by the network when ANY player's command arrives (including our own).
  receive(cmd) {
    const list = this.scheduled.get(cmd.execTick) || [];
    list.push(cmd);
    this.scheduled.set(cmd.execTick, list);
  }

  // Player issues intent now; it executes INPUT_DELAY ticks in the future.
  issue(partialCmd) {
    const cmd = {
      ...partialCmd,
      owner: this.playerId,
      execTick: this.sim.tickNumber + INPUT_DELAY,
      seq: this._nextSeq++,
    };
    this.network.broadcast(cmd); // network delivers to everyone, us included
  }

  // Advance one simulation tick using exactly the commands scheduled for it.
  step() {
    const cmds = this.scheduled.get(this.sim.tickNumber) || [];
    this.sim.tick(cmds);
    this.checksums.push(this.sim.checksum());
    this.scheduled.delete(this.sim.tickNumber - 1); // tidy old entries
  }
}

// A synchronous, loss-free stand-in for real UDP networking. Real netcode adds
// acknowledgements, retransmission, and stalling when a player's commands are
// late — but the SIMULATION contract proven here is identical.
class LoopbackNetwork {
  constructor() {
    this.clients = [];
  }
  connect(client) {
    this.clients.push(client);
  }
  broadcast(cmd) {
    for (const c of this.clients) c.receive({ ...cmd });
  }
}

module.exports = { Client, LoopbackNetwork, INPUT_DELAY };
