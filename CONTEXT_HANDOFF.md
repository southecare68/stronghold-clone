# Context Handoff — read me first (for a fresh Claude Code session)

Paste this to Claude Code when you open this repo, or just let it read the file.
It captures every decision so far so you don't have to re-explain the project.

## What we're building
A **multiplayer castle RTS** (Stronghold-style) from scratch, with our own art.
Focus is **multiplayer**: a rebalanced roster, custom unit point-buy, and new
mechanics that don't exist in Stronghold. We chose to build our own game rather
than mod the closed 2006 engine, which was too limiting.

## Decisions already made
- **Engine:** Godot 4 with **C#** (Unity was the runner-up). Reason: free/open,
  lightweight, gets out of the way of a custom deterministic simulation, and C#
  suits our mixed team. See `ARCHITECTURE.md` for the full rationale.
- **Netcode:** deterministic **lockstep with input delay** — the only practical
  model for an RTS with many units. Only *commands* cross the network, never
  unit state.
- **Determinism rules (non-negotiable):** the simulation uses **fixed-point
  integer math only** — no `float`/`double`, no wall-clock time, no unseeded
  RNG, no hash-ordered iteration. This is what keeps ARM Macs and x86 Linux in
  sync. Rendering may use float; the sim may not.
- **Architecture:** the simulation is **engine-agnostic** (`game/Sim/`, no Godot
  references). The engine only renders it and turns input into commands.

## Current state (what's in this repo)
- `prototype-node/` — the original proof, in Node. **Verified:** two clients
  stay bit-identical for 300 ticks (`node test/sync.test.js`), and the float
  hazard is demonstrated (`node test/float-hazard.test.js`). This is the
  reference behaviour.
- `game/Sim/` — the C# port of that verified core: `Fixed.cs`, `Simulation.cs`,
  `Lockstep.cs` (turns, input delay, stalling, snapshots, the `ITransport` seam),
  plus `TileMap.cs`, `PathFinder.cs`, `Vision.cs` (fog) and `Skirmish.cs` (the
  1v1 start).
  **Built and verified** — see `tests/SimParity`.
- `game/Net/` — engine-agnostic protocol, Godot-free like `Sim/` so it is
  testable with plain `dotnet`: `Wire.cs` (turn/snapshot serialization),
  `MatchCode.cs` (endpoint ↔ join code).
- `game/Scripts/` — the Godot layer. `Main.cs` renders the sim, turns mouse
  input into commands, and interpolates between ticks; `EnetTransport.cs` is
  `ITransport` over a real socket. `Main` exposes `LocalClient`/`RemoteClient`
  so tests can read sim state without a screen.
- `tests/` — four Godot-free console apps that compile `game/Sim/` (and
  `game/Net/`) directly, so they run anywhere `dotnet` does — including the
  Ubuntu x86 box:
  - `SimParity/` — the C# sim reproduces the Node reference exactly (0xB1A7A676)
  - `InputSlice/` — the mouse flow, headless
  - `CommandOrder/` — command ordering is total, not arrival-order dependent
  - `Netcode/` — wire format, join codes, stalling, desync detection, rejoin
  - `Pathfinding/` — map, RNG, and deterministic grid A* (Phase 2 foundations)
  - `PathFollowing/` — units follow smoothed routes; two-client StateChecksum sync
  - `Combat/` — deterministic fighting, RNG in sync, rejoin mid-fight, win/lose
  - `Economy/` — gather/haul/deposit, conservation, two-client sync, rejoin
  - `Buildings/` — placement/cost, footprint blocking, keep drop-off, production
  - `Walls/` — curtain walls, gatehouse open/close, two-client sync, rejoin
  - `Siege/` — destructible buildings, breaching, two-client sync, rejoin
  - `PointBuy/` — data-driven unit designs, budget, stat effects, sync, rejoin
  - `Replay/` — record a match, replay it bit-for-bit, save/load
  - `Fog/` — fog of war: sight, explored memory, and the orders they gate
  - `Audio/` — the sound synthesizer, checked numerically (no speakers needed)

## Toolchain on the Mac Studio (nothing is on PATH — use full paths)
- Godot 4.7.1 .NET: `~/Downloads/Godot_mono.app/Contents/MacOS/Godot`
- .NET SDK 8.0.423: `~/.dotnet` (installed via Microsoft's `dotnet-install.sh`,
  no sudo, no Homebrew on this box). Prefix with
  `export PATH="$HOME/.dotnet:$PATH"`.

## Done so far
1. ✅ **Godot build.** `dotnet build` in `game/` succeeds, 0 errors 0 warnings;
   the editor imports the project clean and the game launches (Metal/Forward+)
   with no runtime errors. Two first-run nits fixed: `StrongholdClone.csproj`
   pinned `Godot.NET.Sdk/4.3.0` (must match the installed editor → 4.7.1), and
   `project.godot` advertised feature `"4.3"` → `"4.7"`.
   ✅ **Confirmed visually under live mouse input** (2026-07-22, screenshots).
   The window titles "Stronghold Clone (DEBUG)", draws 3 blue player-1 and 2 red
   player-2 units at their spawn cells, and a real left-drag box-select put
   white rings on all three player-1 units. A right-click at window-relative
   (400,300) sent exactly those three to world (33,25) — they crossed the map
   and arrived, while player 2's units correctly ignored the order. The HUD read
   `IN SYNC ✓` on every captured frame (ticks 7076 → 11678).
   Motion rate cross-checked against the sim: 1.52 px/tick measured vs the
   1.5 px/tick the constants predict (Fixed.One/8 per tick × 12 px/unit).
   Cosmetic note for later: arriving units stack on the exact same pixel —
   there is no separation/collision yet. That's Phase 2 work, not a bug.
2. ✅ **Port parity proven.** `dotnet run --project tests/SimParity` replays the
   exact `sync.test.js` scenario and gets **0xB1A7A676**, matching Node, plus 11
   intermediate tick checkpoints and a reproducible re-run. Exit code 0.
   The C# sim is a faithful port. If you ever change sim behaviour on purpose,
   re-derive the constant from the Node run — never edit it to make red go green.

   Also `tests/InputSlice` (new): replays Main.cs's mouse flow headlessly —
   box-select in screen space, right-click to a screen point, issue the Move —
   and asserts the two clients agree on **every one of 400 ticks** while the
   three selected units travel to the clicked cell and player 2's units ignore
   the order. `dotnet run --project tests/InputSlice`, exit 0.
3. ✅ **Command ordering made total** — the latent desync that had to die before
   ENet. `Simulation.CanonicalOrder` used to return 0 for two commands with the
   same owner AND type, leaving them in *arrival* order; arrival order differs
   per machine on a real network, so two same-tick same-owner MOVEs could apply
   in opposite orders on two peers. (Note the original diagnosis was slightly
   off in a way that doesn't matter: .NET's sort is unstable in general, but for
   small partitions it insertion-sorts and *does* keep arrival order. Either
   way the result is arrival-order-dependent, which is the bug.)

   Fix: `Client.Issue` stamps a per-client `Command.Seq`, and the comparator is
   now `(Owner, Seq)` — unique, because only a player's own client issues that
   player's commands. `Type` is deliberately no longer a key, so a player's
   commands apply in the order they issued them instead of being regrouped by
   type. Added `Command.Clone()` so no transport can forget to copy a field
   (that footgun is waiting for `EnetTransport`).

   Mirrored in the Node reference so the two don't drift. **Both still produce
   0xB1A7A676** — the fix changes no existing behaviour.

   Guarded by `tests/CommandOrder`, which ships a transport that hands the two
   clients the same commands in OPPOSITE orders. Verified it fails on the old
   code (desync at tick 3, peers disagreeing on a unit's destination) and passes
   on the new. `dotnet run --project tests/CommandOrder`, exit 0.

4. ✅ **EnetTransport — real two-machine play.** Two OS processes, a real ENet
   socket, verified on localhost (see "what's proven" below).

   **The lockstep layer gained turn boundaries, and that is the main change.**
   Commands used to be broadcast individually, which cannot support a stall:
   silence from a peer is indistinguishable from a packet still in flight, so a
   client had no way to know whether to wait. Now every player sends exactly one
   `TurnInput` per tick — *including empty ones* — and a client refuses to
   advance until it holds every player's turn for the tick it is about to run.
   That refusal is the stall. `Client.Step()` is gone, replaced by
   `SendInput()` + `TryStep()`: every client must publish before any client
   consumes, so a single process driving several clients calls SendInput on all
   of them first. `SimParity` asserts it never stalls on a loss-free transport.

   New files: `game/Net/Wire.cs` (explicit little-endian turn serialization —
   `BitConverter` follows host endianness, which is exactly the ARM-vs-x86 trap
   this project exists to avoid), `game/Net/MatchCode.cs`, and
   `game/Scripts/EnetTransport.cs`. `game/Net/` is Godot-free like `game/Sim/`,
   so the protocol is testable with plain `dotnet`.

   Desync detection: every turn piggybacks the sender's checksum for a tick it
   has already completed. A peer that disagrees is caught within a few ticks and
   named — `DESYNC at tick 412: local 0x… != player 2 0x…` — instead of the
   match slowly going strange.

   Join by IP (`--join=ADDR[:PORT]`) or by code (`--code=XXXXX-XXXXX`). A code
   is the host's IPv4 and port in Crockford base32 — a friendlier spelling of an
   address, **not NAT traversal**. A code holding a private address only works
   on that LAN; punching through home routers needs a rendezvous server, which
   is a separate decision.

   ⚠️ **Not yet handled:** a disconnected peer cannot rejoin. The host correctly
   freezes forever (see below), but a reconnecting client would start at tick 0
   against a host at tick 1051. Rejoin needs state transfer or a match restart.

   *What's proven, by two processes on one Mac:*
   - Both instances reached identical ticks and checksums and held `IN SYNC ✓`
     (e.g. tick 551, `0xFF21C713` on both).
   - Commands cross the wire **in both directions**: player 1 commanded from the
     host and player 2 commanded from the joiner both moved on both machines.
     Worth testing separately — client→server and server→client are different
     ENet paths.
   - Selection rings appear only on the machine that selected: local UI state
     stays local, only commands are shared.
   - **The stall rule holds under a real failure.** Killing the joiner froze the
     host at tick 1051, and it stayed at 1051 — not one tick advanced without
     its peer.

   *Three bugs the live run caught that no unit test would have:*
   1. The joiner sent turns before the socket finished connecting. ENet refuses
      the write, so those turns were silently lost — and a lost turn is not a
      dropped frame, it is a tick nobody can ever run. Fixed by refusing to tick
      until the match is connected.
   2. The peer count was double-counted (the connect event *and* a peer-list
      correction), reporting "2/1 connected".
   3. The two connection signals become true in opposite orders on the two ends:
      the event leads on the host, the raw peer list leads on the joiner. Now
      all three signals (both counts plus `GetConnectionStatus`) must agree.

5. ✅ **Visual interpolation.** Units are drawn between their position before the
   last tick and their position now, scaled by how far the frame clock has
   travelled toward the next tick, so 20 Hz motion no longer steps at 20 Hz.

   Entirely inside `Main.cs`. The interpolated value is a float, is never fed
   back, and nothing in the sim can observe it — which is exactly why the sim
   may forbid floats while the renderer uses them freely. `SimParity` still
   prints 0xB1A7A676.

   Everything on screen goes through one `WorldToScreen`, hit-testing included,
   so a box-select catches the units the player can *see* rather than the
   positions the sim is holding up to a tick ahead of the picture.

   The picture trails the sim by up to one tick (50 ms). Extrapolating ahead
   instead has to guess, and guesses wrong every time a unit stops or turns,
   which looks worse than a small constant lag — and there is already 150 ms of
   input delay in the protocol.

   *Verified numerically, not by eye.* `--debug-interp` prints the drawn
   position beside the true one:
   - `a=0.467  was (10.4807, 9.6868)  drawn (10.5290, 9.7196)  sim (10.5841, 9.7571)`
     — and 10.4807 + 0.467 × (10.5841 − 10.4807) = 10.5290, matching to four
     decimals on both axes across sampled frames.
   - The per-tick delta measured (0.1034, 0.0703) has magnitude 0.125, exactly
     the sim's `Fixed.One / 8`.
   - On arrival: `was == drawn == sim == (33.0000, 25.0000)` at a=0.713 — a
     stationary unit interpolates to a no-op, so nothing drifts or overshoots.

6. ✅ **Rejoin after a disconnect.** A returning player is handed the match as it
   stands instead of starting a new one.

   A client that has been away cannot replay the ticks it missed — it never
   received those commands — so it is handed the result: a `MatchSnapshot` of
   pure integer state (tick, next unit id, every unit including its *target*,
   and the sender's checksum).

   Two details that are easy to get wrong and would each cause a permanent stall:
   - **The snapshot carries the host's already-published turns.** Input delay
     means a client commits to turns several ticks ahead and will never send
     them again, so without these the rejoiner waits forever for input that was
     already spoken for. The live run shows "4 turns already in flight" —
     ticks 974–977, exactly `InputDelay + 1`.
   - **The snapshot must reach the wire before any turn built on it.**
     `ReadyToPlay` keeps the host from ticking until the snapshot is sent and
     the joiner from acting until it is adopted; ordering then holds over the
     reliable ordered channel.

   **The state transfer verifies itself.** The rejoiner recomputes the checksum
   after adopting and compares it against the host's. A snapshot that arrives
   wrong is caught at the join instead of becoming an unexplained desync later —
   `tests/Netcode` corrupts one unit by a single fixed-point step (1/65536 of a
   tile) and the join reports it.

   The wire format's reserved header byte became a message kind, so turns are
   byte-for-byte unchanged.

   *Verified live over ENet:* host at tick 974 (`0xDE688200`) with its units
   already moved, joiner killed, a **fresh process** connected and logged
   `joined the match at tick 974, checksum 0xDE688200 verified against our own`.
   It inherited the moved positions it had never seen the commands for, both ran
   on to tick 2217 `IN SYNC ✓`, and the rejoined player could command its own
   units again — moving them on both machines.

   ⚠️ Still true: this is a **2-player** design. Snapshots go to one joiner at a
   time and the host is the only source; 3+ players need a decision about who
   snapshots whom.

7. ✅ **Unit movement follows the pathfinder, with smoothing.** A Move command
   now becomes an A* route the unit walks waypoint by waypoint, instead of
   sliding in a straight line through walls.

   **String-pulling is the load-bearing part**, for two reasons at once:
   - It stops units zig-zagging along tile centres — the route collapses to the
     fewest straight legs that stay clear.
   - It is what protects `0xB1A7A676`. On open ground the first shortcut check
     sees the destination directly, the whole route becomes one leg, and the
     movement maths is bit-identical to the pre-pathfinding sim. `SimParity`
     (which runs on the default open map) still prints 0xB1A7A676.

   **A real design bug the tests caught:** smoothing by line-of-sight alone
   ignores terrain COST. Marsh is passable, so a plain-LOS smoother straightened
   an A* detour right back through the marsh it had been computed to avoid —
   shorter in tiles, more expensive to walk. Fix: `TileMap.HasClearRun` only
   shortcuts across **ground**, never costlier terrain, so cost-optimal detours
   survive smoothing while uniform ground still collapses to one leg.
   (`HasLineOfSight`, the pure-passability version, stays for future vision/
   ranged-fire use.)

   **Checksum split, per the decision below.** `Simulation.Checksum()` is frozen
   (units only, == Node). New `StateChecksum()` covers everything the network
   compares — unit targets, remaining paths, next-id, and the map's fingerprint.
   `tests/PathFollowing` runs two clients over the obstacle map for 600 ticks and
   `StateChecksum` agrees every tick; it also shows two different maps producing
   different `StateChecksum` but identical frozen `Checksum()`, so a mismatched
   map is caught on the first comparison.

   ✅ **Now visible in the window** (2026-07-23). `Main.cs` starts on
   `TileMap.Demo(56)` and draws terrain (ground / rock wall with a gate / lake /
   marsh) plus the selected units' remaining route as a yellow line. Verified by
   screenshot: three units box-selected top-left and ordered across the map
   routed **through the wall gate and around the lake's corner**, the path line
   kinking only at those corners with dead-straight legs between (string-pulling
   working), then arrived stacked at the destination with no clipping through
   wall or water. `IN SYNC ✓` throughout. Terrain draws as one ground background
   rect plus only the non-ground tiles, so it stays cheap.

8. ✅ **Cross-architecture determinism CONFIRMED** (2026-07-23). `SimParity` was
   run on the Ubuntu **x86** box and printed **0xB1A7A676** — bit-identical to
   the ARM Mac, across 300 ticks and all 11 checkpoints. This is the result the
   whole architecture was built to earn: fixed-point-only sim, seeded RNG,
   total-ordered iteration, explicit little-endian wire format — all of it exists
   to make two different CPU architectures agree exactly, and now they provably
   do. The riskiest unknown in the project is retired.

   ✅ **And the live match is now done too** (2026-07-23). A real windowed game
   over ENet between the ARM Mac (host) and the x86 Ubuntu box (join,
   `--join=192.168.0.209`) ran and stayed **in sync** — the full game (economy,
   buildings, combat, siege, point-buy designs), not just the headless sim,
   bit-identical across two CPU architectures over the wire. This is the
   end-to-end validation the whole project was built for; there is no more
   fundamental unknown to retire. (Getting Ubuntu ready needed .NET via
   `apt install dotnet-sdk-8.0` and a manual `dotnet build` in `game/` before the
   first Godot launch — the class-not-found error means the C# assembly wasn't
   compiled yet.)

9. ✅ **Combat + win condition** — the first actual game loop. An Attack command
   targets an enemy unit; the unit chases (re-pathing periodically), strikes in
   melee range on a cooldown, and rolls damage from the **seeded RNG**. Dead
   units are removed in id order; `MatchWinner()` reports the last side standing.
   Right-click an enemy in-game to attack, empty ground to move; health bars
   show over damaged units and the HUD announces the winner.

   **This is the change that forced — and completed — the checksum plumbing the
   whole project had been deferring:**
   - The RNG is now wired and drawn (damage only). Its `State` is game state:
     hashed into `StateChecksum`, carried in `MatchSnapshot`, restored on rejoin.
   - `Simulation.Restore` and the snapshot wire format now carry the RNG and the
     full unit state (combat fields + remaining paths).
   - Netcode switched from `Checksum()` to `StateChecksum()` (see above).

   **0xB1A7A676 is untouched**, on purpose and by design: a unit only fights once
   it has a TargetId, which only an Attack order sets, so a Move-only match makes
   **zero** RNG draws. `tests/Combat` asserts this directly ("move-only makes no
   RNG draws"), and `SimParity` still prints the constant.

   `tests/Combat` proves the rest: a 1v1 resolves, an outnumbered side loses,
   a unit acquires the next foe after a kill, a Move breaks off combat, **two
   clients roll the identical damage across a 500-tick battle and agree on the
   winner**, and **a mid-fight rejoin resumes the RNG in lockstep** (the proof
   the RNG state travels correctly). Verified visually too: blue army crossed the
   demo map, engaged, and won with the HUD banner, `IN SYNC ✓`.

   Combat is deliberately minimal — melee only, no unit collision/separation (so
   units stack when converging), no attack-move (Move ignores enemies; only
   Attack engages), one unit type. All fair game to extend.

10. ✅ **Economy — gather / haul / deposit.** Resource nodes (Wood/Stone/Food)
   sit on tiles and deplete; a Gather order sends a worker to cycle node → full
   load → owner's drop-off → deposit → repeat, until the node is empty. Per-owner
   stockpiles; a Move order calls a worker off the job. Right-click a node in-game
   to gather; nodes shrink as they deplete, workers show a coloured dot when
   hauling, and the HUD shows your stockpile.

   Followed the same discipline as combat, no new surprises: all integer, no RNG
   (gathering is not random), id-ordered iteration, stockpiles/drop-offs kept in
   `SortedDictionary` so every machine hashes owners in the same order. New state
   (nodes, stockpiles, drop-offs, per-unit worker fields) went into
   `StateChecksum()` and `MatchSnapshot` — never `Checksum()`, so `SimParity`
   still prints 0xB1A7A676 (a Gather-free match makes no economy changes). The
   Gather order reuses `Command.TargetId` for the node id, so the turn wire format
   was unchanged.

   `tests/Economy` proves it: a worker banks a load with **conservation checked**
   (what leaves the node = banked + carried, nothing created or lost), a small
   node is emptied to the last unit, a Move breaks off the job, a gather with no
   drop-off is refused, **two clients run the identical economy for 800 ticks in
   sync**, and a **rejoin rebuilds the whole economy** (nodes, stockpiles,
   drop-offs) and stays locked. Verified live too: three workers gathered wood to
   a stockpile of 150, `IN SYNC ✓`.

   Deliberately minimal: no unit collision (workers stack on a node), drop-off is
   a bare tile (a stand-in for a keep/town-centre until buildings exist), no
   worker/soldier distinction (any unit can gather or fight).

11. ✅ **Buildings — the Phase 2 capstone.** A Build order places a structure
   (Keep 3×3, Barracks 2×2) with its footprint validated (in-bounds, passable,
   unoccupied) and its cost charged to the stockpile; a refused build spends and
   places nothing. Footprints **block the pathfinder** — units route around them,
   the castle-defining behaviour — via a mutable occupancy overlay on the TileMap
   (which the terrain fingerprint deliberately ignores, since occupancy is
   derived from the buildings list that IS in StateChecksum). A Keep sets its
   owner's drop-off (replacing the bare-tile stand-in). A Barracks takes Train
   orders that cost wood and queue soldiers, produced after a build time and
   spawned on the footprint's edge.

   Same discipline: integer, id-ordered, new state (buildings, nextBuildingId)
   into `StateChecksum()` and `MatchSnapshot` — never `Checksum()`, so SimParity
   still prints 0xB1A7A676. Build/Train reuse `Command.TargetId` (building type /
   building id), so the turn wire was unchanged. Added `Simulation.AddResource`
   for match-setup starting stockpiles.

   **Two real bugs the tests caught, both about a building's centre being
   walled-in:** a 3×3 keep's centre is two tiles from the nearest standable tile,
   so (1) a worker could never get within the 1.5-tile deposit range of it, and
   (2) it couldn't even PATH to the blocked centre. Fixed by depositing at a
   larger `DropOffRange` AND setting a keep's drop-off to a reachable perimeter
   tile, not the buried centre.

   `tests/Buildings` proves placement/cost/validation, footprint blocking (a path
   that ran straight now detours and never crosses the footprint), keep-as-
   drop-off, barracks production, move-only-changes-nothing, **two-client
   build+train sync**, and a **rejoin that rebuilds buildings AND re-stamps their
   map occupancy**. Verified live: placed a barracks with `B`, right-clicked to
   train, wood went 200 → 130 (−40 barracks, −2×15 soldiers), soldiers spawned,
   `IN SYNC ✓`.

   In-game: `[B]`/`[K]` place a barracks/keep at the cursor; right-click your own
   barracks to train.

**Phase 2 is essentially complete** — map, pathfinding+smoothing, combat+win,
economy, and buildings, all deterministic and cross-architecture-verified. What
remains is polish and Phase 3 (the castle identity: walls/gatehouses, the custom
unit point-buy, your own mechanics).

12. ✅ **Unit separation — render-only.** Units that share a tile now fan out on
   screen instead of drawing on one pixel. **Decision (2026-07-23): render-only**,
   the same class as interpolation — the sim is untouched, so `0xB1A7A676` and
   every test stay exactly as they were. (Sim-level separation would change plain
   movement, which the parity scenario exercises by sending units to a shared
   target, so it would have forced a re-derive of the constant; that trade wasn't
   worth it for a cosmetic fix.)

   In `Main.cs` only: each frame, units are grouped by the tile their sim position
   rounds to, and a group of more than one is laid out in a stable **sunflower
   (phyllotaxis)** pattern ranked by id — a stable function of sim state, so no
   per-frame jitter. The offset goes through the single `WorldToScreen`, so clicks
   and box-select land on the unit drawn under the cursor.

   ⚠️ Consequence to remember: units still share a tile for **pathfinding,
   combat, and gathering** — separation is purely visual. Formations, space-
   blocking, and chokepoint behaviour would need sim-level collision, which is a
   deliberate re-derive-the-constant decision for later (or for Phase 3).

13. ✅ **Phase 3 begins — walls & gatehouses.** Two new building types: **Wall**
   (1×1, cheap stone, meant to be laid tile by tile into a curtain wall) and
   **Gatehouse** (1×1) with an **open/close gate** — the new mechanic. A gate's
   `Open` flag toggles its tile between walkable and blocking, via a `ToggleGate`
   command (owner-only). Built on the existing footprint-occupancy overlay, so
   walls block the pathfinder for free; the gate just flips its own tile's block.

   Same discipline: `Open` into `StateChecksum()` and the snapshot; Build/Toggle
   reuse `Command.TargetId`, so the turn wire is unchanged; `0xB1A7A676` intact
   (buildings are opt-in). The one subtlety, handled: `Restore` re-blocks every
   footprint EXCEPT an open gate, so a rejoiner doesn't rebuild an open gateway
   as a solid wall.

   `tests/Walls`: a wall seals a corridor, a gate opens/closes the gap (path
   appears/disappears and runs through the gate tile), enemies can't work your
   gate, cost/validation, two-client build+toggle sync, and a rejoin that
   restores gate state AND occupancy. Verified live: laid a wall line with a
   gatehouse (`W`/`G` keys), right-clicked the gate to open it — the render
   switched from a solid block to open jambs.

   In-game: `[W]` wall, `[G]` gatehouse at the cursor; right-click your own gate
   to open/close it.

   ⚠️ **Walls are currently indestructible** (like all buildings). That makes them
   a turtling tool with no counterplay — fine as a first slice, but the next task
   fixes it.

14. ✅ **Siege — destructible buildings.** Buildings now have HP (Keep 600,
   Barracks 250, Wall 200, Gatehouse 250). An `AttackBuilding` order sets a unit's
   `TargetBuildingId`; the combat phase closes to the wall and batters it with the
   same RNG damage as unit combat. At 0 HP the building is destroyed: its footprint
   becomes walkable rubble, a razed keep stops being a drop-off, and it leaves the
   list. Only enemy buildings can be targeted; besiegers clear a destroyed target
   the next tick. Distance is measured to the nearest footprint tile, so a unit
   against any face of a big keep is in range.

   Same discipline: `TargetBuildingId` and building HP into `StateChecksum()` and
   the snapshot; `AttackBuilding` reuses `Command.TargetId` (building id), turn
   wire unchanged; `0xB1A7A676` intact (opt-in). Guard added: the economy deposit
   handles a drop-off vanishing mid-haul (its keep razed) by standing the worker
   down instead of throwing.

   `tests/Siege`: a wall is battered down, breaching it re-opens the sealed
   corridor (rubble is passable), you can't besiege your own buildings, razing a
   keep drops its drop-off, move-only leaves buildings alone, **two clients batter
   in sync for 700 ticks**, and a **rejoin carries building HP and the siege
   through the breach**. Verified live: sent three units across the demo map to
   right-click the enemy keep — they crossed, besieged, and **razed it**, then
   stood on the cleared site, `IN SYNC ✓`.

   In-game: right-click an enemy building with units selected to besiege it;
   building HP bars show once a structure is hit.

   **Walls are now a real mechanic** — build them to buy time, breach them to get
   through. The castle-siege loop is complete.

15. ✅ **Custom unit point-buy** — the distinctive roster mechanic. Unit stats are
   now DATA, not hardcoded: every unit is built from a `UnitDesign`
   (Hp/Damage/SpeedStat/RangeStat/Cooldown), and movement, combat and siege all
   read from the unit's design instead of shared constants. Players compose a
   roster of designs, each spending a fixed **point budget** (`MaxDesignPoints`,
   43 — exactly the default soldier's cost) allocated across stats; `RegisterDesign`
   refuses anything over budget. So a glass cannon and a walking tank can cost the
   same points, spent differently.

   **The hard part was doing this WITHOUT breaking anything.** The refactor
   touched the most-verified code (combat/movement), but design 0 — the default
   soldier — reproduces the old constants EXACTLY (Damage 10 → `NextInt(8,13)`,
   SpeedStat 5 → `One/8`, etc.), so all 11 prior test projects and `0xB1A7A676`
   are unchanged. The barracks train-queue became a queue of design ids so
   different designs can be produced; Train reuses `Command.X` for the design id
   (turn wire unchanged). Designs + unit `DesignId` go into `StateChecksum()` and
   the snapshot.

   `tests/PointBuy`: the budget is enforced (over-budget refused), the default
   soldier is provably unchanged, a fast design outruns a slow one, a tanky one
   outlasts a fragile one, a high-damage one kills faster, **two clients agree
   with mixed designs in a 600-tick battle**, and a **rejoin carries the roster**.
   Verified live: a demo roster (Soldier/Runner/Brute), `1/2/3` picks the design,
   the HUD shows its stats and point cost (43/43), and trained units render at
   different sizes by HP — `IN SYNC`, wood charged exactly.

   In-game: `1/2/3` choose the design a barracks trains; the HUD shows it.

   ⚠️ The point WEIGHTS and example designs are placeholder balance — tune freely.
   An interactive point-allocation UI (sliders, pre-match roster editor) is
   deferred; designs are registered at match setup for now.

**Phase 3's two big pillars are done** — the castle identity (walls, gatehouses,
siege) and the custom point-buy roster. The deterministic, cross-architecture
RTS is feature-complete against the original brief.

16. ✅ **Replay system.** Lockstep makes a match a function of terrain + start
   state + command stream, so recording those reproduces it EXACTLY. `Replay`
   (game/Net) records a match, plays it back, and serialises to a few KB;
   `ReplayRecorder` attaches to a `Client` (via the `ITickRecorder` interface in
   Sim, so the Sim layer keeps no dependency on Net) and captures each tick's
   commands.

   Two payoffs beyond "watch your game back": it's a **determinism check** (a
   playback whose checksum differs from the live run is a desync the recorder
   caught), and a **debugging superpower** (a desync seen between two machines can
   be reproduced on ONE machine from the recorded commands and bisected — the
   natural companion to the pending live ARM↔x86 match).

   Plumbing reused/added: `Simulation.Snapshot()` (a pure sim snapshot, which
   `Client.CaptureSnapshot` now builds on) and `Restore(MatchSnapshot)`;
   `TileMap.CopyTiles()`/`FromTiles()`; `Wire.WriteCommand`/`ReadCommand` +
   public `PutInt/GetInt` (single source of truth for command bytes, shared by
   turns and replays).

   `tests/Replay`: a 400-tick match (economy/combat/buildings/mixed designs on
   the demo map) reproduces bit-for-bit — verified **every tick**, not just the
   final number — survives save/load byte-identically, and refuses malformed
   bytes. Verified live in Godot: played a match, `F5` saved
   `user://last.shrep` (661 ticks, `0xB0408CA2`), and `--replay=<path>` watched
   it back to `✓ reproduced exactly` on the same checksum.

   In-game: `F5` saves the match so far (also auto-saved on exit);
   `--replay=user://last.shrep` watches it back (passive — no input).

## Where it stands
**Feature-complete against the original brief, and validated end to end.** A
deterministic, cross-architecture multiplayer castle RTS: economy, buildings,
combat, siege, working gatehouses, a custom point-buy roster, rejoin, desync
detection, and replays — proven bit-identical on ARM and x86 both headlessly
(SimParity) and in a **live windowed ENet match** between the two machines. 15
Godot-free test suites guard it all; `0xB1A7A676` still holds.

✅ **Camera (pan & zoom)** added, engine-layer only. Mouse wheel zooms toward the
cursor, middle-drag or the arrow keys pan; the view is clamped to the map.
Implemented as a manual transform (`ApplyCameraTransform` via `DrawSetTransform`
in `_Draw`, inverted by `ScreenToCanvas` for input) rather than a `Camera2D`
node, so rendering and hit-testing share ONE formula — box-select and orders land
correctly at any zoom — and the HUD Label, a separate node, stays screen-fixed
for free. Works while watching a replay too. No sim/test changes.

✅ **Ranged units.** The `RangeStat` already drove attack distance; this made it a
real, legible feature. A **Shot** stream (`Simulation.ShotsThisTick`) records each
blow's from/to — **transient render candy**: cleared every tick, never hashed,
never snapshotted, never read back, so it's checksum-neutral (`0xB1A7A676`
holds). `Main.cs` turns long-range shots into flying arrows (render-only, replays
for free). Added an **Archer** design to the demo roster (RangeStat 8 = 4 tiles,
low HP; key `4`), and the HUD shows the range stat. `tests/PointBuy` proves a
ranged design damages a target from beyond melee reach without closing, while a
melee unit must move in. Verified live: archers loosed yellow arrows at the enemy
from range and killed a unit. (Range's point WEIGHT is unchanged — 1 pt/half-tile
— so if archers prove too strong, bump the weight in `UnitDesign.PointCost`.)

✅ **Minimap.** Bottom-right panel showing the whole battlefield — terrain,
resource nodes, buildings and units — plus a **viewport rectangle** that tracks
the camera and shrinks as you zoom in, clipped to the panel so it never spills
outside. **Click it to jump the camera** there; the click is hit-tested in screen
space before any gameplay click, so it never orders units, and it works while
watching a replay. Drawn after resetting the camera transform
(`DrawSetTransform(identity)`), which is what keeps it pinned to the corner at
any zoom. Engine-layer only — no sim or test changes.

✅ **A real skirmish map** (128×128 — five times the area of the old 56-tile demo,
and far larger than the window, which is what the camera and minimap were for).

- `TileMap.Skirmish(size)` — hand-authored, **no RNG**: every coordinate is a
  fixed fraction of `size`, so the same size always builds the identical map and
  the `StateChecksum` map fingerprint agrees on every machine. A 3-tile rock
  ridge runs north–south down the middle with **three passes** (at 25%, 50% and
  75% height), so the terrain shapes the fight instead of decorating it: the
  middle pass is the short road and is flanked by marsh aprons that slow anyone
  taking it, while the outer passes are clean but long. Two lakes sit off the
  centre line and two outcrops break up the open ground. `TileMap.Demo` is
  untouched — the existing tests still reference it.
- `Sim/Skirmish.cs` — **the starting position, defined once.** It lives in the sim
  rather than `Main.cs` for two reasons. Determinism: every machine must build a
  byte-identical world before tick 0, so there must be one definition, not one
  per call site. And a layout can be wrong in ways the compiler cannot see — a
  node dropped in a lake, a keep straddling the ridge — which is silent in-game
  (you just find a patch nobody can work). Putting it here lets the headless
  tests place the real start and check it. That is not hypothetical: **the south
  contested node was in the water** when first written, and the test caught it.
  Mirrored keeps and parties either side of the ridge, two safe patches behind
  each base, and a contested pair out by the north and south passes.
- `tests/Pathfinding` gained `TheSkirmishMapIsPlayable` (bases open, a route
  exists west→east, and it crosses the ridge rather than rounding its ends —
  checked at sizes 96/128/160) and `TheSkirmishStartIsSound` (both keeps place,
  all six nodes are on open ground and reachable, the roster registers, and two
  independent setups agree on `StateChecksum`).
- **Renderer fixes the bigger map forced.** `DrawTerrain` now culls to the visible
  tile range — a few hundred rects a frame instead of sixteen thousand. The
  minimap bakes terrain **once** into a tile-per-pixel `ImageTexture` and blits
  it, rather than drawing every tile every frame. And `ClampCamera` now keeps the
  **view** on the map rather than just the centre: clamping the centre alone was
  harmless when the whole map fit in the window, but at this size it let you
  scroll half a screen of void into frame (it did, on the first run). An axis too
  short to fill the window is centred instead.
- The camera opens on **your own keep**, not the map centre, which is now a long
  way from anything you own.
- Verified live at 1200×800: start on your keep with the view stopping at the map
  edge; a minimap click jumps to the enemy base and clamps to the east edge;
  zoomed out the whole battlefield is visible and centred; three units ordered
  across the map routed between the marsh aprons, **through the middle pass**,
  and held it — `IN SYNC ✓` at tick 3339, keeping full 20 Hz throughout.
- Sim-side additions only ever *add* state when used, so `0xB1A7A676` holds and
  all 13 suites pass.

✅ **Fog of war — a rule, not a screen effect.** The fork here was real and went
to the user: render-only fog (cheap, checksum-free, but a modified client sees
everything and you can still order attacks into the dark) versus fog in the
simulation. **Sim-level with order gating was chosen.**

- `Sim/Vision.cs` splits the two things both called "fog":
  - **Explored** — every tile a player has EVER seen. Accumulates, never clears.
    Genuine accumulated state: it depends on the whole history of the match, two
    machines could disagree, and it gates orders. So it is **hashed into
    StateChecksum and travels in a MatchSnapshot**. Stored as one bit per tile
    (512 uint words for a 128x128 map, per player).
  - **Visible** — what a player can see right now. A pure function of where their
    units and buildings stand, so two machines agreeing on positions cannot
    disagree about it. Rebuilt at the top of every Tick and deliberately **not**
    hashed and **not** snapshotted — hashing derived state adds no detection
    power and only invites an ordering bug.
- **Sight is blocked by rock, not by "impassable".** New `TileMap.HasSightLine`
  (integer Bresenham, same family as the existing traces) blocks on rock only:
  a lake is impassable but you can see clean across it. Buildings are excluded
  too — opaque walls sound right until your own castle blinds you, and it would
  let a player darken their opponent's view by building. This is what makes the
  skirmish ridge more than decoration.
- **What it gates:** Attack needs the target VISIBLE; AttackBuilding, Gather and
  Build need the ground EXPLORED (a structure or a wood you scouted stays known —
  that is the point of scouting). Move is never gated, or you could not scout at
  all. `AcquireNearestEnemy` skips what the owner cannot see, but a target already
  engaged is NOT dropped when it slips into fog — a soldier that forgot its
  opponent the instant it stepped behind a rock would look broken.
- **`FogEnabled` is opt-in, like every other checksum-affecting feature.** Fog
  changes which orders are legal, and the older suites were written against a sim
  without it, in scenarios that place units far apart and order them at each other
  immediately. Turning it on globally would silently rewrite what those tests
  test. `Skirmish.Setup` switches it on for real matches; the flag itself is
  hashed, since two machines disagreeing about it would disagree about legality.
- **The bug this shook out:** `Restore` first recomputed visibility with
  accumulation on. Exploration folds in at the TOP of a tick, so by snapshot time
  the units have since moved — the rejoiner ended up knowing a sliver more of the
  map than the sender. Split into `Update` (accumulates) and `RecomputeVisible`
  (does not); restore uses the latter.
- **Renderer** (`Main.cs`): unexplored black, remembered dimmed, visible clear;
  enemy units hidden unless visible; enemy buildings and resource patches
  remembered once explored; arrows only when an end of the shot is in sight; the
  same rules on the minimap, whose fog layer is a tile-per-pixel texture rebuilt
  at the TICK rate, not the frame rate. Click hit-testing uses the same tests, so
  a right-click on a hidden enemy falls through to a move order. `F` reveals the
  map — display only, and no fog at all in a replay, which is watched from
  outside the match.
- **The layout bug fog exposed:** the home resource patches sat just outside the
  keep's opening sight, so neither player could gather until they had scouted
  their own back yard. Moved to ±8 tiles; `tests/Fog` now pins "both players open
  with two workable patches, and the contested ones still have to be found".
- `tests/Fog` (14th suite): sight geometry is round not square, rock blocks sight
  and water does not, explored accumulates while visible does not, every gated
  order is refused and then accepted once seen, aggro does not reach through
  rock, two clients agree for 400 ticks with fog on, wiping one client's memory
  IS caught by StateChecksum, explored survives snapshot and the wire bit for
  bit, a truncated snapshot is refused, and a fogged match replays exactly.
- Verified live: opening view is your base disc alone with both home patches
  workable; scouting leaves a corridor of remembered ground in a visibly
  different shade; the sight disc **clips flat against the ridge** with the rock
  face visible and everything behind it black; `F` reveals the map and a
  right-click on an enemy then produces an Attack that **the simulation refuses**
  — the units do not move; moving into unexplored dark still works. `IN SYNC ✓`
  at tick 5175, full 20 Hz throughout.

✅ **Sound — generated, not sourced.** There are no audio files in the repo, and
that was the design decision rather than a shortcut. The project's premise is its
own art; a handful of short effects are cheap to describe as noise and envelopes
and expensive to store as binary blobs nobody can diff, review or retune. The
"assets" are source code.

- `game/Audio/Synth.cs` — engine-agnostic, exactly like `Net/`. Produces plain
  16-bit mono PCM at 22.05 kHz from oscillators, one-pole filters and percussive
  envelopes. 13 effects: select, move/attack order, melee hit, bowshot, arrow
  hit, death, build place, train complete, deposit, gate, collapse, denied. Each
  reads as a recipe — a comment saying what it is trying to be, numbers saying
  how — so retuning is a one-line change. Every sound is normalised to the same
  peak and ramped to silence over its last 4 ms (a buffer that stops mid-waveform
  ends on a step, and a step is a click — the commonest way synthesised audio
  sounds cheap). A private xorshift, deliberately NOT `Sim.Rng`, so nobody ever
  has to wonder whether generating a sound could nudge a damage roll.
- `game/Scripts/Sound.cs` — the Godot half: wraps the buffers in `AudioStreamWav`,
  runs a 24-voice pool (oldest stolen when full — a battle should sound like a
  battle, not a queue), applies a per-effect dB trim (the deposit tick sits 13 dB
  under a collapsing wall, because it fires constantly), and enforces a per-effect
  minimum gap so twenty simultaneous blows do not stack into one crunch.
- **Positional**, via an `AudioListener2D` parked at the camera centre. The game
  draws through a manual transform rather than a `Camera2D`, so Godot has no
  listener to infer and one has to be supplied. The audible radius is tied to the
  visible half-width, so zooming out widens what you can hear.
- **Fog gates hearing, not just seeing.** A fight you cannot see makes no sound;
  a ranged exchange is two sounds in two places and each end is heard only if
  THAT end is visible. Audible fog would hand back exactly the information the
  fog exists to withhold.
- **Events come from diffing the simulation between ticks**, not from hooks
  inside it. The sim stays free of presentation concerns, and a replay makes the
  same noises for nothing because it reproduces the same transitions. Order
  acknowledgements are the exception — played on the CLICK, since the protocol
  has three ticks of input delay and a late acknowledgement feels like being
  ignored.
- **A refused order now says so.** `Denied` fires when the client can predict the
  simulation will refuse — an unaffordable or unplaceable building, or an attack
  on a unit only visible because `F` revealed the map. Previously a refused order
  was indistinguishable from a click that never registered.
- `tests/Audio` (15th suite): every effect is audible, non-clipping and sanely
  long; none begins or ends on a step; rendering twice is bit-identical; the PCM
  encoding round-trips little-endian; and the sounds that must be told apart
  measurably are — brightness (zero-crossing rate) separates bowshot from rumble
  and move order from attack order, and ONSET brightness separates a sword crack
  from timber, which whole-buffer brightness cannot because an impact is a bright
  crack over a long low body. `--write <dir>` dumps all 13 as .wav files, which is
  how a human checks the half a test cannot.
- **Three things the work shook out**, the third a real pre-existing bug:
  1. The build-place transient was bright enough (2.6 kHz) to be confusable with a
     sword landing; rolled off to 1.2 kHz, which the onset test now pins.
  2. A newly-built gatehouse groaned open the instant it landed — the observer
     read "no previous state" as "changed". Caught in the live log, not by a test.
  3. **`SiegeBuilding` recorded each blow as landing on the building's CENTRE**,
     while reach is measured to the nearest footprint tile (`DistToBuilding`). So
     a soldier standing against a 3x3 keep logged a 2.4-tile strike, which the
     renderer classified as ranged: melee siege has been **drawing arrows** ever
     since ranged units were added, and it made a battering ram sound like
     archery. The shot now lands on the part of the structure the unit is actually
     against — which is more accurate on screen too. Presentation only
     (`ShotsThisTick` is transient and never hashed), so no checksum moved and all
     15 suites still pass. Sound found a rendering bug that was invisible to the
     eye for two features.
- Verified in-engine with `--audio-log`: 13 effects synthesised and 24 voices
  ready at startup; select, move order, train, gather, denied (twice — an
  unaffordable build AND a build into unexplored dark), build place, gate toggle,
  train complete and deposit all fired at the right world positions with the
  right trims (deposit quietest at −14.4 dB, collapse loudest at −1.4 dB). A full
  battle produced 11 melee hits and 3 deaths at the exact tiles the enemies stood
  on, and a ranged exchange split correctly into two sounds in two places —
  BowShot at the archer, ArrowHit where the arrow landed. All 13 effects observed
  firing in-engine.
- **Not verified by me: whether it sounds good.** I cannot listen. The numbers,
  the timing, the positions and the mix levels are checked; the aesthetics are
  yours to judge — run the game, or `tests/Audio -- --write` and play the files.

✅ **Music — composed, not sourced.** Same premise as the effects, one step
further: the score is generated too. The design decision that matters is the
**split between composing and rendering**.

- `game/Audio/Music.cs` — `Compose(mood)` returns a list of `Note`s (start,
  length, pitch, voice, gain); `Render(mood)` turns those into PCM. Effects did
  not need this, but music does, because **the interesting mistakes in music are
  musical**. "Is every pitch in the mode?" and "does the harmony change on the
  bar line?" can only be asked of notes — by the time it is a waveform, a wrong
  note is just a number. So `tests/Audio` asserts against `Compose` and never has
  to guess from a spectrum.
- **D Dorian**, the medieval mode; its raised sixth is the whole character and is
  why it does not sound like generic film minor. Battle drops to Aeolian — one
  flattened note, and the brightness goes straight out. Progression is
  i–VII–III–IV with no leading tone anywhere, so it turns over forever without
  asking to resolve, which is what background music for a strategy game has to do.
- **Instruments:** Karplus-Strong for the melody and bass (a delay line of noise,
  averaged as it circulates — ten lines for a convincing plucked lute), detuned
  partials for the pad, a low fifth for the drone, pitch-swept sine for the kick,
  filtered noise for the snare.
- **Seamless looping**, bought two ways. Tempos (72/100/140) are chosen so
  `SampleRate*60/BPM` is an EXACT integer — a tempo like 132 leaves a fractional
  sample per beat that accumulates into an audible stumble once a cycle. And
  notes running past the end **wrap round to the start** rather than being cut,
  so a phrase finishes over the top of the repeat. The drone's envelope is one
  full sine cycle over the loop, periodic by construction.
- `Scripts/MusicPlayer.cs` — two `AudioStreamPlayer`s cross-fading over 2.2 s with
  an **equal-power** curve (linear cross-fades dip in loudness through the middle,
  because power goes as the square). Non-positional, and mixed under the effects:
  a soundtrack that buries the sound of your own army dying is worse than silence.
- **Adaptive, off the same observations everything else uses** — no new hooks in
  the sim, so a replay scores itself. Battle while blows land or your units are
  committed; Tension when anything of theirs is visible; Calm otherwise. The mood
  is sticky on the way down (6 s hold) so a lull does not make the score stutter.
  And because Tension reads YOUR visibility, the music can never reveal an enemy
  before the fog would have.
- Tests: every pitch is in the mode (49/73/121 notes, none stray), Battle uses the
  flat sixth Calm never touches, pads are exactly one bar long on a bar line,
  every note starts inside the loop, the moods differ in tempo/density/kit in the
  right direction, every tempo divides the sample rate exactly, music peaks below
  the effects, and — the one that matters most — **the step across each loop point
  is no larger than the largest step inside the track**, which is what "no click"
  actually means for this material.
- Verified live: `[music] 3 tracks composed: Calm 26.7s@72bpm, Tension 19.2s@100bpm,
  Battle 13.7s@140bpm`, and the HUD walked the full cycle — `calm` at the start,
  `tension` the moment the enemy came into sight, `battle` during the fight, and
  back to `calm` once they were dead and the hold expired. `N` toggles it.
- **Still not verified by me: whether any of it sounds good.** I cannot listen.
  All 13 effects and all 3 tracks are dumped to `~/Desktop/stronghold-sfx/`.

## Immediate next tasks (choose by taste — the core is done)
17. **Polish & depth:** an interactive point-buy/roster UI; more maps (the
   `Skirmish` pattern generalises — it takes a size and uses no RNG); ambience and
   victory/defeat stingers (the synth and the composer have the building blocks);
   menus; unit/building selection panels.
18. **Multiplayer robustness (Phase 4 in ARCHITECTURE.md):** lobby/matchmaking to
   replace hand-typed IPs, lag tolerance/adaptive input delay, spectating (falls
   out of the replay format), reconnect polish. The live cross-arch match and the
   replay system are already done.

## Phase 2 so far: the map and the pathfinder
Deliberately started with the piece everything else stands on — buildings occupy
tiles, resource nodes sit on tiles, combat happens between things positioned on
tiles — and, just as deliberately, with the piece that changes **no** checksum,
so the decision below could stay open while real work got done.

- `game/Sim/Rng.cs` — xorshift32. `System.Random` is banned in the sim: its
  algorithm is not contractually fixed across .NET versions and it is
  clock-seeded by default. **When anything first draws from it, its `State`
  becomes game state** and must be checksummed and carried in `MatchSnapshot`.
- `game/Sim/TileMap.cs` — immutable terrain (ground / water / rock / marsh),
  integer costs in tenths so a diagonal is 14 against an orthogonal 10.
  Authored from text rows for tests, or generated deterministically from a seed.
  *Not checksummed, on purpose:* terrain never changes during a match, so it
  cannot diverge. **The day terrain becomes destructible, the mutable part must
  go into `Simulation.Checksum()` and `MatchSnapshot` the same day.**
- `game/Sim/PathFinder.cs` — grid A*, all integers, octile heuristic.

  The trap here is the one `tests/CommandOrder` exists for, in a new place: on
  open ground dozens of routes tie for shortest, and if the tie-break depends on
  discovery order, two machines send units different ways and desync. So the
  open set is ordered by **(F, H, tile index)** — a total order fixed by the
  map's geometry, never by what was discovered first. Heap keys are stored in
  the heap rather than read back from the cost array, or a later improvement
  would break the heap invariant and stop returning the cheapest tile.

  Diagonals use the **strict** corner rule (both adjacent orthogonals must be
  clear), so units walk around a wall corner instead of shaving it. Work is
  bounded by `MaxExpansions`; a client that spends 400 ms on one tick has
  stalled every other player in the match.

`tests/Pathfinding` covers correctness and, more importantly, determinism: same
query repeats exactly, a fresh instance on a freshly generated map agrees tile
for tile, and two **pinned golden routes** across open ground would break the
moment the tie-break changed. Being integer-only, those routes must come out
identical on the Ubuntu x86 box too — so this test is a second cross-architecture
probe alongside `SimParity`.

## The golden constant problem — DECIDED and IMPLEMENTED: legacy hash
**`Simulation.Checksum()` is frozen and `StateChecksum()` now exists.**
`Checksum()` hashes tick number and per-unit id/owner/x/y/hp, still equals
Node's **0xB1A7A676**, and Phase 2 must not add a single field to it.
`StateChecksum()` covers everything that can diverge — unit targets, remaining
paths, next-id, the map fingerprint, and (as they arrive) stockpiles, buildings,
RNG state. **`StateChecksum()` is what turns piggyback and what desync detection
compares; `Checksum()` is only the frozen regression guard.** `SimParity` keeps
using `Checksum()`, so the verified movement core stays pinned while the game
grows around it.

✅ Netcode wiring done (with combat, below): the network layer now exchanges and
verifies `StateChecksum()` everywhere — turn checksums, snapshot capture/adopt —
and `MatchSnapshot` carries full unit state (paths + combat) plus the RNG state.
`Checksum()` is now used ONLY by `SimParity`.

⚠️ **The subtle part, and it is not the added fields.** Wiring movement onto the
pathfinder threatens 0xB1A7A676 through unit **positions**, which `Checksum()`
already covers. If units follow A* waypoints tile by tile, they zigzag through
tile centres and land on different coordinates than today's straight-line
movement — and the parity scenario goes red even though no field was added.

The fix is one we want anyway: **string-pull the path** (drop any waypoint the
unit has clear line of sight past). On open ground that collapses the whole route
to a single waypoint at the destination, movement maths is bit-identical to
today, and 0xB1A7A676 survives. It also stops units zigzagging along tile centres
in open field, which looks wrong regardless. Build the smoothing at the same time
as the path-following, not after — otherwise the parity test goes red and the
temptation to "just re-derive it" arrives at the worst moment.

The reasoning behind the decision, kept because the trade-off will come up again:
`Simulation.Checksum()` currently hashes tick number and per-unit id/owner/x/y/hp
and nothing else. Add a stockpile, a building list, or unit facing, and the hash
changes — so `SimParity`'s **0xB1A7A676** goes red the first time real Phase 2
state lands. That is not a bug to work around; it is the test doing its job.

This repo's stated rule is "change both sides together and re-derive the constant
from the Node run", which implies mirroring economy, buildings and combat in
JavaScript. That was right when the Node prototype was the reference for a hand
port; it is a poor deal now, because it means writing the game twice.

Three ways forward:
- **(a) Keep a legacy hash.** `Checksum()` stays exactly as it is — units only,
  still 0xB1A7A676, still comparable to Node — and a new `StateChecksum()`
  covering everything becomes what the network compares. Preserves the
  regression guard on movement maths without freezing the game's shape. Costs a
  second hash to keep straight.
- **(b) Retire the Node parity.** Freeze `prototype-node/` as history, re-derive
  the constant from C#, and let `SimParity`'s real job become the
  cross-architecture comparison (Mac and Ubuntu produce the same number).
  Simplest, but loses the independent reference.
- **(c) Mirror Phase 2 in Node.** Keeps the letter of the rule. Expensive, and
  the Node prototype was never meant to be the game.

Chose **(a)**, 2026-07-22: it is the only one that keeps the verified movement
core pinned while the game grows around it, without writing the game twice.

**Next task, now unblocked:** unit movement follows pathfinder routes (with
string-pulling, per the warning above), then economy, buildings, combat,
win/lose.

## How to run
- Prototype proof: `cd prototype-node && node test/sync.test.js`
- Port parity:    `export PATH="$HOME/.dotnet:$PATH" && dotnet run --project tests/SimParity`
- Input slice:    `export PATH="$HOME/.dotnet:$PATH" && dotnet run --project tests/InputSlice`
- Command order:  `export PATH="$HOME/.dotnet:$PATH" && dotnet run --project tests/CommandOrder`
- Netcode:        `export PATH="$HOME/.dotnet:$PATH" && dotnet run --project tests/Netcode`
- Two-machine match (the same on both boxes, `dotnet` must be on PATH or Godot
  cannot find hostfxr and crashes on startup):
    host:   `export PATH="$HOME/.dotnet:$PATH" && Godot --path game -- --host`
    joiner: `export PATH="$HOME/.dotnet:$PATH" && Godot --path game -- --join=<host LAN IP>`
  The host prints its address and match code on its waiting screen. Note the
  `--` : Godot only passes arguments after it through to the game.
- Game (editor):  open `game/` in the Godot 4.7 .NET editor, press Play.
- Game (CLI):     `~/Downloads/Godot_mono.app/Contents/MacOS/Godot --path game`

## Machines
Mac Studio = main dev box. Keep the **Ubuntu (x86) desktop** in rotation as the
cross-architecture multiplayer test partner against a Mac (ARM). This pairing is
a real asset — it catches determinism bugs most solo devs can't test for.
