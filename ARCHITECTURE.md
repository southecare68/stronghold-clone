# Building a Multiplayer Castle RTS — Architecture & Roadmap

A working plan for a Stronghold-style real-time strategy game with online
multiplayer, built from scratch. This document is the reference we build
against; it explains the engine choice, the core architecture, and the phased
plan. It ships alongside a **runnable, tested prototype** (in `/src` and
`/test`) that already proves the hardest part works.

---

## 1. Engine recommendation: Godot 4 (with C#)

You asked me to recommend one. For a **mixed team** (some strong coders, some
designers/artists) building a **deterministic-lockstep RTS** with **your own
art**, my recommendation is **Godot 4 using C#**, with **Unity** as the
close second.

Reasoning:

| Factor | Godot 4 (C#) | Unity | Unreal |
|---|---|---|---|
| Cost / licensing | Free, open source, MIT — no royalties or seat fees ever | Free tier, but licensing history makes teams nervous | Royalty on revenue |
| Fit for a *custom* deterministic sim | Excellent — lightweight, gets out of the way, you own the loop | Good, but you fight its physics/update model | Poor — its netcode is replication-based, not lockstep; heaviest to bend |
| Approachable for a mixed team | Yes — small editor, C# is readable, fast iteration | Yes — biggest tutorial ecosystem | Steeper (C++/Blueprints) |
| 2D / 2.5D castle RTS visuals | Strong 2D + capable 3D | Strong | Overkill |
| RTS lockstep learning resources | Growing | Most existing examples target it | Fewest |

The single most important point: **the simulation is engine-agnostic** (see §2),
so the engine only renders and captures input. That means the engine choice is
*reversible and low-risk* — if Godot ever pinches, the sim ports to Unity almost
unchanged. Pick Unity instead if your strongest coders already know it well; the
architecture below is identical either way.

---

## 2. The core architecture: separate the SIMULATION from the ENGINE

This is the decision everything else depends on.

```
   ┌─────────────────────────────────────────────────────┐
   │  ENGINE LAYER (Godot/Unity)                          │
   │  - draws units, plays sound, runs the camera & UI    │
   │  - reads mouse/keyboard -> produces COMMANDS          │
   │  - interpolates visuals between sim ticks (smooth)   │
   └───────────────▲───────────────────┬──────────────────┘
                   │ state to draw     │ player commands
   ┌───────────────┴───────────────────▼──────────────────┐
   │  SIMULATION LAYER (pure C#, no engine references)     │
   │  - fixed-point math ONLY (no float) -> deterministic  │
   │  - tick(state, commands) -> newState                  │
   │  - identical result on every machine, every run       │
   └───────────────▲───────────────────┬──────────────────┘
                   │ commands           │ commands
   ┌───────────────┴───────────────────▼──────────────────┐
   │  NETWORK LAYER                                        │
   │  - lockstep: send only COMMANDS, never unit positions │
   │  - input delay + per-tick checksum for desync detect  │
   └───────────────────────────────────────────────────────┘
```

Three rules that keep this honest:

1. **The simulation never touches floating point, wall-clock time, engine
   randomness, or hash-order collections.** Only fixed-point integers, a fixed
   tick rate, a seeded deterministic RNG, and ordered iteration. This is what
   makes every machine agree.
2. **Only commands cross the network** ("move units [3,7] to (30,25)"), never
   results. A 1,000-unit battle sends a handful of bytes, not 1,000 positions.
3. **Every machine simulates the same tick with the same commands**, and we hash
   the world state each tick to catch any divergence instantly.

The prototype in this folder implements all three in miniature and **proves they
hold** (see §4).

---

## 3. Why lockstep, and how input delay works

An RTS can have thousands of units. You cannot stream their state like a
shooter streams a few players. So RTS games (Stronghold, Age of Empires,
StarCraft) run **deterministic lockstep**: every client runs the full game,
exchanging only the players' orders.

Because orders need time to reach everyone, a command issued on tick *N* is
scheduled to execute on tick *N + delay* (the prototype uses 3). Every client
receives it before that tick arrives, so all clients execute the identical
command set in the identical order. Real netcode adds acknowledgements,
retransmission, and "stall if a player's orders are late," but the simulation
contract is exactly what the prototype demonstrates.

---

## 4. What the included prototype already proves

Run it yourself (Node.js, no dependencies):

```
node test/sync.test.js         # two clients stay bit-identical for 300 ticks
node test/float-hazard.test.js # shows why floats would desync
```

Verified output:

- **Two independent simulations, exchanging only commands, produced identical
  state checksums on every one of 300 ticks** — and the same run replayed from
  scratch produced the identical final checksum. That is lockstep working.
- The float test shows the same seven numbers summing to **0.9 one way and 0.6
  the other** — a stark illustration of why the simulation forbids floating
  point. The integer version is order-independent, hence safe.

The prototype is written in Node so I could run and verify it here; the logic is
a direct 1:1 map to the C# you'll write in Godot/Unity (fixed-point helpers,
`Simulation.Tick()`, command scheduling, FNV checksum). Porting it is the first
task of Phase 1.

---

## 5. Roadmap

**Phase 0 — Foundations (done here):** engine-agnostic deterministic sim +
lockstep model, proven headlessly. ✅

**Phase 1 — Playable 1v1 vertical slice (the milestone you chose):**
- Port the verified sim core to C# inside a Godot project.
- Render units as sprites; interpolate between ticks for smooth motion.
- Box-select + right-click-move UI producing commands.
- Real transport: two clients over LAN/UDP (or Godot's high-level multiplayer)
  with the input-delay queue and a live desync checksum readout.
- Goal: two people on two machines each command a few units in the same match,
  provably in sync. This de-risks everything.

**Phase 2 — Core RTS gameplay:** resources/economy, buildings (keep, walls,
production), a unit roster with combat, win/lose conditions. Pathfinding for
many units (flow fields or grid A*), still fully deterministic.

**Phase 3 — The "castle" identity & your new features:** walls/gatehouses,
your custom unit point-buy system, and the mechanics that make this *your*
game rather than a clone. Because you own the sim, "new features that don't
exist in Stronghold" are fully in reach here — unlike modding a closed engine.

**Phase 4 — Multiplayer robustness & release plumbing:** lobby/matchmaking or
join-by-code, reconnect handling, lag tolerance, a replay system (falls out of
lockstep almost for free — just record the commands), and anti-cheat basics.

**Phase 5 — Content & polish:** your art pipeline, audio, maps, menus, balance.

---

## 6. Honest expectations

A polished, shippable multiplayer RTS is a large, multi-person effort measured
in many months to years — I want to be straight about that. But it is very
buildable *incrementally*, each phase is playable, and the riskiest unknown
(deterministic lockstep) is already solved and proven above. I can write real
code with you at every step: the C# port, the Godot project, netcode, gameplay
systems, and tools.
