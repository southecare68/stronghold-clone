// simulation.js — The engine-agnostic deterministic game simulation.
//
// This is the heart of the whole project. It knows NOTHING about rendering,
// Godot, Unity, windows, or the network. It is a pure state machine:
//
//     newState = tick(state, commandsForThisTick)
//
// Given the same starting state and the same commands, it ALWAYS produces the
// same next state, on every machine. The engine's job (later) is only to draw
// this state and collect player input; the network's job is only to deliver
// commands. Keeping the sim isolated like this is what makes lockstep feasible.

const FP = require('./fixed');

// ---- Command types -------------------------------------------------------
// A command is a player's INTENT ("move these units here"), never a result.
// Only commands travel across the network — never unit positions.
const CommandType = { MOVE: 'MOVE' };

// ---- World ---------------------------------------------------------------
class Simulation {
  constructor() {
    this.tickNumber = 0;
    this.units = []; // sorted by id, always iterated in id order (determinism!)
    this._nextId = 1;
    this.UNIT_SPEED = FP.fromInt(1) / 8; // fixed-point units per tick
    this.ARRIVE_EPS = FP.fromInt(1) / 4; // "close enough" threshold
  }

  // Spawn is itself deterministic: ids are assigned in a fixed order.
  spawnUnit(ownerId, xInt, yInt) {
    const u = {
      id: this._nextId++,
      owner: ownerId,
      x: FP.fromInt(xInt),
      y: FP.fromInt(yInt),
      tx: FP.fromInt(xInt), // target x
      ty: FP.fromInt(yInt), // target y
      hp: 100,
    };
    this.units.push(u);
    return u;
  }

  _applyCommand(cmd) {
    if (cmd.type === CommandType.MOVE) {
      // Deterministic: unitIds is a sorted list; we look them up in order.
      for (const id of cmd.unitIds) {
        const u = this.units.find((v) => v.id === id);
        if (u && u.owner === cmd.owner) {
          u.tx = FP.fromInt(cmd.x);
          u.ty = FP.fromInt(cmd.y);
        }
      }
    }
  }

  // Advance the world exactly one tick.
  // `commands` is the full, agreed-upon set of commands for THIS tick from ALL
  // players. We sort them into a canonical order so every machine applies them
  // identically regardless of network arrival order.
  tick(commands) {
    const ordered = [...commands].sort(canonicalCommandOrder);
    for (const cmd of ordered) this._applyCommand(cmd);

    // Integrate movement. Iterate units in id order (they already are).
    for (const u of this.units) {
      const dx = u.tx - u.x;
      const dy = u.ty - u.y;
      const dist = FP.vlen(dx, dy);
      if (dist > this.ARRIVE_EPS) {
        const step = Math.min(this.UNIT_SPEED, dist);
        // Normalize (dx,dy) then scale by step — all fixed-point integer math.
        u.x += FP.div(FP.mul(dx, step), dist);
        u.y += FP.div(FP.mul(dy, step), dist);
      } else {
        u.x = u.tx;
        u.y = u.ty;
      }
    }

    this.tickNumber++;
  }

  // ---- Checksum --------------------------------------------------------
  // A 32-bit FNV-1a hash over the raw integer state of every unit. If two
  // machines are truly in sync, this number is identical every tick. The
  // instant it diverges we've detected a desync (a bug, or a float sneaking in).
  checksum() {
    let h = 0x811c9dc5;
    const mix = (n) => {
      // Fold a 32-bit integer into the hash, byte by byte.
      n = n | 0;
      for (let i = 0; i < 4; i++) {
        h ^= (n >>> (i * 8)) & 0xff;
        h = Math.imul(h, 0x01000193);
      }
    };
    mix(this.tickNumber);
    for (const u of this.units) {
      mix(u.id);
      mix(u.owner);
      mix(u.x);
      mix(u.y);
      mix(u.hp);
    }
    return h >>> 0; // unsigned
  }
}

// Canonical ordering so all machines apply commands in the same sequence.
// This must be a TOTAL order: (owner, seq) is unique, because seq is handed out
// by the issuing client and only that client issues that player's commands.
// Returning 0 for two distinct commands would leave them in ARRIVAL order, which
// differs per machine on a real network — a silent desync. (Array.sort being
// stable in JS does not save us; stable means "keeps arrival order", and arrival
// order is the untrustworthy part.) Mirrored in game/Sim/Simulation.cs; the C#
// side has the regression test, tests/CommandOrder.
//
// type is deliberately NOT a sort key: a player's own commands must apply in the
// order that player issued them, never regrouped by type.
function canonicalCommandOrder(a, b) {
  if (a.owner !== b.owner) return a.owner - b.owner;
  return a.seq - b.seq;
}

module.exports = { Simulation, CommandType };
