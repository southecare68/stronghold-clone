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
  `Lockstep.cs` (turns, input delay, stalling, snapshots, the `ITransport` seam).
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

   Still worth doing eventually, but no longer load-bearing: a **live** ARM↔x86
   match over ENet (`--host` on one, `--join=<LAN IP>` on the other), watching
   for `DESYNC`. The headless proof means that if a live match ever desyncs, the
   sim is innocent and the fault is in the transport.

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

## Immediate next tasks (in order)
12. **Unit collision / separation** — the one rough edge that keeps recurring:
   units stack on the same pixel when they converge (a move target, an enemy, a
   node, a barracks spawn point). A deterministic separation/local-avoidance step
   would make crowds read correctly. Integer-only, into the movement phase.
13. **Phase 3** (ARCHITECTURE.md): walls & gatehouses, the custom unit point-buy,
   and the mechanics that make this its own game. Buildings + blocking already
   lay the groundwork for walls.

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
