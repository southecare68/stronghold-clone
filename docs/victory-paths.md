# Victory paths, spies, and the road there

This is the design of record for how a match is *won*. It consolidates the two
design docs (`victorypaths.pdf`, `spy.pdf`) into a build plan grounded in the
sim we already have, and records the decisions that shape near-term work.

The guiding call: **keep the foundations, and let the paths guide sequencing.**
The whole design is economy-primary — every path is a race along the same
economic axes (tall/wide, hoard/flow, extract/sustain), so the deterministic
economy sim *is* the engine the paths race on. The paths are scoring lenses laid
over that engine, plus a referee that watches the meters. Nothing about the
food/gold/popularity/territory systems is thrown away.

---

## How you win

Achieve the **HIGH** goal of one path **and** the **MEDIUM** goal of a *different*
path. No single-stat cheese — every winner has touched two paths. With four
players filling two path-slots each, every path is contested roughly twice.

### The four paths

| Path | Crown | HIGH | MEDIUM |
|---|---|---|---|
| **Economic** | The Merchant | Hold **1,000,000 gold** for 30 min | Bank **500,000 gold** once |
| **Religious** | The Faithful | Convert **75%** of your people (+ faith of 2 other territories) | **50%** conversion (+ 1 other territory) |
| **Domain** | The Sovereign | **5,000 population** + hold **5 territories** | **2,500 pop** + **2 territories** |
| **Science** | The Scholar | Complete the tech tree + **2 wonders** | 1 wonder + 75% of the tree |

*(These are the design's numbers. The shipped targets are scaled to the prototype so
the four races are comparable — see [Balance](#balance).)*

### The shared laws (what keeps it balanced)

- **Dual goal** — high in one path + medium in another.
- **Population floor** — no crown counts until the realm holds at least **200
  people** (`MinPopToWin`, `Victory.cs`), a floor under *every* path so a tiny
  settlement can't snap a quick win. The metric (bar, 80% announce) still fills
  below it; only the crown's hold is gated — the hold accrues, and the buzzer
  fires, solely for a realm over the floor. It sits just above Domain's own "great
  population" HIGH (180), and the goals HUD shows `⚠ needs 200 pop` on any path met
  while under it.
- **Everything is announced** — cross ~80% of any HIGH goal and the whole realm
  is told, opening the counterplay window.
- **Sustained-hold windows** — every HIGH goal must be *held* for a set time, not
  snapped shut, so spies and raids have something to bite.
- **One clock** — a single match timer decides the winner at the buzzer; its
  length is the master dial (short favors tempo, long favors scaling).
- **Game calendar** — a cosmetic Year/Month clock (`TicksPerMonth`, `GameYear`/
  `GameMonth`/`GameMonthName` in `Victory.cs`), shown top-right. A month is 20s of
  play, a year twelve of them; purely derived from the shared tick, so it never
  desyncs and adds nothing to the checksum. Opens on Year 1, Month 1 (January).
- **Diminishing returns** — over-investing one track has a falling ceiling, so
  hybrids stay attractive.
- **War feeds the attacker** — every act of war advances your own path (loot /
  annex / sabotage); no spite-only griefing. Military is a shared **tool**, not a
  fifth path.
- **No elimination** — you cannot kill the king. Razing a realm's last keep exiles
  and resets it, never removes it (see Exile & Return below). War is a setback lever,
  not a knockout, so an early rush can't kingmake anyone out of the scored race.

### The spy counter-web

Two tiers. One dagger per crown, plus one for the shared sword. A spy is never
generic harassment — it is the specific answer to one way of winning, or to the
one tool everyone shares.

| Spy | Counters | Offense |
|---|---|---|
| The Embezzler | Economic | Skims gold from the announced hoard into yours |
| The Inquisitor | Religious | Discredits & re-converts a slice of the flock, dropping their % (**the only thing that pushes conversion backward**) |
| The Saboteur | Science | Wrecks a wonder's progress or steals a researched tech |
| The Agitator | Domain | Incites emigration — peasants drift toward *your* land |
| The Assassin | **War itself** | Kills the commander of a raid/invasion/garrison, stalling the attack |

---

## Implementation status

### Shipped — the victory spine + faith (Phase 0 + 1)

The referee and the first scored metric are live in the sim and fully tested
(`tests/Victory`). Deterministic, snapshotted, and checksummed like everything
else; the frozen units-only parity constant (`0xB1A7A676`) is untouched.

- **Faith** (`FaithIdx` on the per-owner stock array). Opens at a starting
  congregation (`StartFaith` 25%), rests there with no church (`BaseFaith`), and
  drifts toward the share a realm's churches can reach — each `Church` ministers
  to `ChurchSeats` peasants; when total reach covers the populace, faith climbs
  toward 100%. Reversible (let population outrun your churches and it slips back),
  which is the seam the Inquisitor plugs into. Conversion settles on the
  popularity cadence in `ResolveRealm`. **Not** coupled to approval yet — that's
  an available tuning lever, deliberately left off so the metric stays clean.
- **The Church building** (`BuildingType.Church`) — timber + stone, passive
  infrastructure (no worker), the only thing that raises faith.
- **The spine** (`game/Sim/Victory.cs`): `VictoryPath` enum, per-path HIGH/MEDIUM
  thresholds, a pure `Progress(owner, path)` read for the HUD, per-owner
  hold-timers + medium/announce latches (all on the stock array), the 80%
  announcement (`VictoryEvents`), the dual-goal win check, and an optional match
  clock (`MatchClockTicks`, 0 = off). All four paths are wired; the metrics that
  exist today are gold (Economic), faith (Religious), and population (Domain).

### The tech tree — the victory structure (shipped: spine + all four branches + the spy web + HUD)

Per `tech.pdf`, **the tree *is* the victory structure**: the four paths are branches
of one research web, and a branch's **capstone is what unlocks its HIGH goal**. The
spine and the Religious branch are in and tested (`tests/Tech`, `game/Sim/TechTree.cs`
+ `Tech.cs`):

- **Research points** (`ResearchIdx`) bank every realm tick at a `ResearchPace`
  (base + Roads), on the stock array like faith/gold.
- **The web** (`TechTree.cs`): a shared trunk (Roads → Chapel/Market unlocks) and
  the full Religious branch — Shrine → the Holy Order **fork** (Missionaries |
  Zealotry, pick one) → Cathedral → **★ Grand Temple** capstone. Nodes carry
  prereqs, fork groups, and a capstone flag; the researched set is a 128-bit mask
  on the stock array.
- **The dual-goal, as economics**: an **escalating cross-branch cost**
  (`CrossBranchPenalty` per off-branch node) makes a second branch dear, and a
  **capstone pick-limit** (`CapstoneLimit = 1`) means you can only capstone *one*
  branch — so "depth in one + a dip into a second" is enforced by the tree itself.
- **Capstone-gates-HIGH**: `Victory.Progress()` now requires a branch's capstone
  (`TechTree.CapstoneFor`) before its HIGH goal counts. **75% faith no longer wins
  on its own — you must research the Grand Temple.** Branches with no capstone yet
  (Economic/Domain/Science) stay metric-only until ported.
- **Research command** (`CommandType.Research`) takes through the normal charged,
  validated path; the **AI is path-aware** — `EnableAi(owner, level, path)` assigns
  a crown (default Religious, so `AiSim` is unchanged), and the bot climbs *that*
  branch to its capstone and raises its structures: churches, wonders, or new keeps.
  A match with several bots spreads them across the crowns (by owner id). It also
  researches its branch's **⚔ war-tool**, so its fights feed its crown; a **Hard**
  bot additionally climbs the **Spy Guild**, funds espionage with an offsetting tax,
  and looses the dagger that answers its rival's leading path. It also **defends**:
  each path's own counter (Cathedral / Banking House / Printing Press / Provincial
  Keeps) already rides the climb to that capstone, so the one shield it reaches for
  specially is the **Bodyguard**, rushed the moment a rival trains the Assassin. And
  it **reacts to the 80% announcements**: once a rival is announced on a crown (the
  latched `Progress().Announced`), the bot locks its counter-dagger onto *that* path
  and keeps it suppressed, over the merely-leading heuristic. Proven in
  `tests/AiPaths` — a Hard bot reaches every capstone and drives every metric,
  climbs the Spy Guild to fire a dagger, rushes the Bodyguard when threatened, and
  focuses the Inquisitor on a rival announced near the faith crown.
- **Economic branch** (`EconomicIncome`): a gold engine on top of tax — Trade Post
  pays a steady flow, the Guild Charter fork adds Monopoly's flat high margin *or*
  Bourse's per-building breadth, Banking House compounds interest on the hoard, and
  the **Grand Exchange** capstone floors the income and unlocks the gold HIGH. Added
  to gold each realm tick; a **Tech HUD** (C key) lets a human spend research, node
  by node, across every branch.
- **The market** (`Market.cs`, `BuildingType.Market`): a freely-buildable trading
  hall — the *manual* counterpart to the Economic branch's passive `EconomicIncome`.
  It trades five goods (Wood, Stone, Food, Iron, and a market-only **Weapons**
  commodity) for gold at a fixed ±25% spread with bottomless supply/demand. Two
  modes: a **Trade** command for a one-shot lump (capped by gold/stock), and a
  per-good **standing order** (`SetTradePolicy` — *Buy up to N* / *Sell above N*)
  that `AutoTrade` settles every realm tick, so a set market runs the economy
  hands-off. Weapons are meaningful, not just a commodity: a barracks **arms a
  recruit from a stocked weapon in place of wood** (0 weapons ⇒ byte-identical to
  the old wood-only recruit path, so frozen `SimParity` is untouched). Stock grew
  by appending (`WeaponsIdx` + 5 policy slots, `StockWidth` 33→39), so no index
  moved and the snapshot round-trips unchanged. **Mercenaries** (`HireMercenary`,
  `MercRoster`): the market also hires trained soldiers outright for gold — no
  peasant, no barracks, no muster — so a rich realm turns its hoard straight into an
  army, bypassing the population/food gate a trained army lives under (scouts aren't
  for sale). The merc musters at the realm's first market on a deterministic free
  tile. **Wages are the fairness valve** (`PayMercenaryWages`, `IsMercenary`): every
  mercenary draws pay each realm tick (≈ hire price / 50), settled before anything
  else, and any the treasury can't cover DESERT (oldest kept, id order). So a
  gold-bought army is bounded by *sustainable income*, not the hoard, and its wages
  drain the very treasury an Economic player is racing to 70k — the more income, the
  more troops, but the more it costs to hold them, with rivals able to attrit the
  company (each kill is gold you must re-spend). Hiring an unregistered design is
  refused (no pay-for-one-get-another). HUD: a palette building + a trade board (Hire
  section + live wage bill) on selecting your market. `tests/Market` (incl. wages,
  desertion, and twin-client determinism over trades and hires).
- **Science branch**: Scholar's Hut → Library → the University fork (Engineering =
  cheaper wonders | Scholarship = faster tree) → Printing Press → the **Academy**
  capstone, which unlocks **Wonders** (`BuildingType.Wonder`) — a grand, expensive
  buildable, gated on the Academy, whose cost **escalates** with each one standing
  (`BuildCostFor`). A wonder **rises over `WonderBuildTime`** (`Construction` on the
  building): it stands visible — and **sabotageable** — while it builds, and only
  counts toward the crown once finished (`WonderCount` = complete only). It visibly
  emerges from the ground as it nears done. The Science HIGH is **two finished
  wonders** (the MED is one); research nodes along the way quicken `ResearchPace`.
- **Domain branch + multi-territory**: Farmstead → Husbandry → the Settlement fork
  (Homesteads = ×4 housing | Colonists = faster growth) → **Provincial Keeps** →
  the **Sovereign's Court** capstone. Provincial Keeps lets you **found a new keep**
  through the build palette — each keep is a new **territory** (`TerritoryCount`),
  costs dear, and must sit `KeepSpacing` clear of your others (a real territory, not
  a cluster); the first keep keeps its drop-off so the original economy holds.
  Homesteads multiplies `PopulationCap`, Husbandry/Colonists add arrivals while a
  realm grows. The Domain HIGH is **population + 5 territories** (capstone-gated).
  *Peaceful expansion only for now — conquest/annexation (taking a territory by
  force) lands with the war layer.*
- **The spy counter-web** (`Spy.cs`, War branch): Muster → Spy Guild → the five
  spies + Bodyguard. A spy is trained by research, costs gold, sits on a cooldown,
  and is fired at your rival through a `Spy` command (`FirstRival` auto-targets the
  lone enemy). Each is the ONE thing that pushes a rival's metric backward —
  **Embezzler** skims the hoard, **Inquisitor** knocks faith down, **Saboteur**
  wrecks a wonder, **Agitator** incites emigration, **Assassin** cuts down a
  soldier — and each is blunted by the target's own **Tier-III counter** (Banking
  House / Cathedral / Printing Press / Provincial Keeps), the Bodyguard blocking the
  Assassin outright. The War branch is a shared tool (no capstone, no cross-branch
  penalty). A **Spies HUD** (X key) fires them; cooldowns and gates show on each.
- **War-tool payoffs** (`WarPayoff`) — "war feeds the attacker". Each branch's ⚔
  node turns an enemy your soldiers cut down into fuel for that path: **Privateers**
  (Economic) pillage gold into your hoard, **War Loot** (Science) strip wood & stone
  to fund wonders, a **Crusade** (Religious) emboldens the faith. Hooked at both
  combat kill-sites; zero unless researched, so a match with no war-tool tech plays
  exactly as before. **Conquest** (Domain) is the fourth — it takes a whole keep 👇.
- **Conquest/annexation** (`AnnexKeep`, Domain war-tool node `Conquest`): once
  Conquest is researched, a keep your army strikes down is **annexed, not razed** —
  it stands battered under its new lord, becomes a new **territory** (feeding
  Domain's "5 territories" *by force*), and the old owner's **idle folk within
  `AnnexRadius`** change hands (the population payoff). Without the tech a felled
  keep falls as before, so existing sieges are untouched. Units stop battering a
  keep the moment it turns friendly; an "⚔ seized a territory" toast fires. This is
  Domain's "or taken by force" — the aggressive player's road to the census crown.
- **Exile & Return** (`Exile.cs`): you cannot kill the king. When a seated realm loses
  its **last keep** it is not eliminated (there is no last-keep-standing win) — it
  flees into exile: `BeginExile` razes the fallen territory and resets the realm to a
  bare opening (starter wood/food, zero gold, holds + 80% latches + spy cooldowns +
  market policies cleared) while **keeping the researched tech mask and banked
  MEDIUMs** so the comeback has teeth; after `RegroupTicks` a fresh keep + starter
  camp `Reseat` at the map's most isolated buildable tile (`FindExileSite`, farthest
  from every standing keep). Two stock slots gate it (`EverSeatedIdx`, `ReseatTickIdx`),
  so it rides the hash/snapshot and never touches the frozen units-only Checksum (no
  seated realm loses a keep in the parity scenario). Emits `Exiled`/`Refounded` events
  → HUD toasts, and the camera follows the human's king to the new seat.
  **The Avenger** (`RaiseAvenger`, Skirmish design 5) is the deterrent that makes the
  killing blow risky: on exile a single immense champion (600 hp / 35 dmg / fast) is
  raised right where the last keep fell (`_fallenKeepTile`, a transient per-tick
  record), in the attacker's midst. It is exile-only — a new `UnitDesign.Trainable`
  flag (false) keeps it off the barracks roster (train command + HUD both skip it) and
  exempts it from the point budget in `RegisterDesign`; `MercRoster` never lists it, so
  it can't be hired either. Renders taller (non-trainable → 1.6× scale).
  `tests/Exile` (exile-not-eliminate, raze+reset, knowledge/medium carry-over, far
  refound, the never-seated guard, the Avenger rising + un-trainable, twin-client
  determinism through exile and return).
- **The Scout** (`Skirmish` design 4, `UnitDesign.Sight`/`Stealth`): a recon unit —
  the fastest legs on the field (`SpeedStat` 14), a far-wider eye (`Sight` 13 vs the
  usual 7), and **stealth**, but frail and weak (40 hp, 4 damage). Sight became a
  per-design stat: `Vision` takes a `sightOf` resolver so each unit lights its own
  radius. Stealth lives in `CanSeeUnit`: a stealth unit is made out by an enemy only
  with a watcher inside `StealthDetectRange` (`DetectorWithin`) — fog-gated, so an
  omniscient (fog-off) view and the parity scenario are unchanged, and combat/orders
  see through it only at close range. Both are kept OUT of the point-buy budget (a
  role, not muscle) and ride the design roster's hash and snapshot. The scout's reach
  compounds with the cautious march — enemies it reveals feed the danger field every
  friendly unit routes around — and its own stealth means the enemy's cautious armies
  never route around IT (they can't see it to fear it).
- **Movement options** (`Simulation` Move command, `PathFinder`): a plain move is
  one direct, string-pulled route (the Stronghold default, and what keeps the frozen
  parity scenario bit-identical). Two flags ride the Move command's `TargetId`:
  **append** (shift — queue a waypoint after the current route; the unit pops the
  next stop on arrival, `Waypoints` + `AdvanceToNextStop`) and **cautious** (alt —
  route around known enemies). Caution builds a per-owner **danger field** from
  visible enemy soldiers (`BuildDangerMap`, `DangerRadius`/`DangerPeak`) that A*
  adds as extra enter-cost, and the string-puller is taught not to straighten a
  detour back through danger (`LineCrossesDanger`). Caution is **dynamic**: every
  `RerouteInterval`, any cautious unit whose road ahead now crosses danger re-paths
  from where it stands to the same stop (`ResolveCautiousReroute`), so a threat that
  only comes into view mid-march — a patrol crests a hill, fog lifts on an ambush —
  bends the route long after the first click. Both the queue and the flag are hashed
  and snapshotted; the HUD draws beacons over each stop (blue direct, amber
  cautious). `tests/Movement` covers the chain, the berth, the mid-march reroute,
  the snapshot, and twin-client determinism.

### Wired but dormant (waiting on later phases)

- **Territories.** `TerritoryCount(owner)` counts live keeps — 1 in every current
  match. Domain's "5 territories" and Religion's "2 other territories" clauses
  already score *through* it, so they light up the moment conquest starts minting
  extra keeps, with no change to the scoring code. **This is the multi-territory
  seam** (see the decision below).
- **Science** is a wired stub: a fourth path slot returning zero progress until
  its tech branch (Library → University → Academy → Wonders) is filled in.

---

## Decision: the multi-territory model (peaceful expansion shipped; conquest next)

> **Status:** the seam below is now live **both ways** — a Domain player founds new
> keeps (each a territory) once Provincial Keeps is researched, **and** annexes an
> enemy's keep by force once Conquest is researched (see the conquest note below).

Two paths depend on holding more than one territory, but our world today is one
home territory per player. Rather than build conquest now or stub the goals away,
we **fix the shape now and implement it in Phase 3**:

- **1 keep = 1 territory.** A territory is the region anchored on a keep (the same
  anchor `HomeRect` already uses — which is why the border stays put as you build
  inside it). An owner holds as many territories as they have live keeps.
- **`TerritoryCount` is the single seam.** Every cross-territory clause reads
  through it. Growing an owner past one keep — by building a second keep in
  unclaimed land, or by **annexing** a defeated rival's keep — automatically makes
  Domain's and Religion's multi-territory goals reachable.
- **Faith stays per-realm for now**, aggregated at the owner. When territories
  become plural, faith becomes per-territory and Religion's "+ the faith of N
  other territories" reads the neighbors' shares. The `Faith(owner)` API is the
  aggregation point that will grow a territory argument.
- **What Phase 3 actually builds:** a `Territory` record keyed by keep; conquest
  that transfers a keep's ownership (and its territory, population, and buildings);
  `HomeRect` generalized from "merge all my keeps into one box" to "one box per
  keep." No scoring code changes — only the world underneath it.

---

## Roadmap

The tech tree reframes the roadmap: the remaining work is **filling in branches**
on the shipped tech spine. Each is a plug-in, not a rewrite.

| Phase | Scope | Status |
|---|---|---|
| **0** | Victory spine — metrics, 80% announce, hold-timers, match clock | ✅ shipped |
| **1** | Faith metric + the Church | ✅ shipped |
| **T** | **Tech spine** — research economy, the web, capstone-gates-HIGH, cross-branch cost + **Religious branch** ported, AI climbs it | ✅ shipped |
| **T-ui** | Tech-tree HUD — research readout, a node panel to spend it, capstone/branch progress | ✅ shipped |
| **2** | **Economic branch** — Trade Post → Guild Charter fork → Banking House → **★ Grand Exchange**, paying a trade income (flow, margin, interest) on top of tax; capstone gates the gold HIGH | ✅ shipped |
| **4** | **Science branch** — Scholar's Hut → Library → University fork → Academy → **Wonders** (the new buildable); HIGH = 2 wonders, MED = 1, capstone-gated | ✅ shipped |
| **3** | **Domain branch + multi-territory** — Farmstead → Husbandry → Settlement fork → Provincial Keeps → **★ Sovereign's Court**; **found new keeps** (each a territory), Homesteads multiplies housing, Husbandry/Colonists speed growth; HIGH = pop + 5 territories | ✅ shipped (peaceful expansion) |
| **5** | **The spy counter-web** — Muster → Spy Guild → the five spies + Bodyguard; each dagger pushes one rival metric back, blunted by that branch's Tier-III counter | ✅ shipped |
| **6** | **Conquest/annexation** — take a keep by force → its territory & population (Domain's ⚔ war-tool) | ✅ shipped |
| **7** | **The rest of the war-tool payoffs** — the other branches' ⚔ nodes (Privateers loot gold · War Loot funds wonders · Crusade emboldens the faith) | ✅ shipped |

The tech HUD comes next so a human can actually spend research (the AI already
does). Spies land last on purpose: an Embezzler with no announced hoard, or an
Inquisitor with no conversion score, has nothing to counter.

### Reserve (first expansion)

**Splendor / Beauty** — the "beautiful castle" fantasy, a genuinely different
scoring shape (adjacency & symmetry), countered by an Arsonist.

---

## Tuning knobs (all in one place)

- Faith: `StartFaith`, `BaseFaith`, `ChurchSeats`, `ConvertRate` (`Simulation.cs`).
- Goals: `EconHighGold`/`EconMedGold`, `RelHighFaith`/`RelMedFaith`,
  `DomHighPop`/`DomMedPop`/`DomHighTerr`/`DomMedTerr` (`Victory.cs`).
- Windows: `AnnounceAt` (80%), `HoldTicksFor(path)`, `MatchClockTicks`
  (`Victory.cs`). Hold windows are in ticks (`TickRate` = 20/s).
- **Match length**: `Simulation.PaceScale` — one dial that scales the victory holds
  (`HoldTicksFor`) and research cost (`ResearchCostFor`) together; since crowns are
  capstone-gated, this paces the whole game. An instance setting (a match setting like
  `FogEnabled`) carried in the snapshot + hash: **default 1** (the original brisk
  ~15-30 min matches, so every test runs full-speed), and the game sets it to **6** at
  setup (`World3D`, `--pace=N` to override) for ~2-hour matches. Realm cadence is
  untouched, so the economy still ticks every 2s.
- Population floor: `MinPopToWin` (200, `Victory.cs`) — the peasant count every
  crown requires; gates the hold accrual and the buzzer, not the metric itself.
- Exile: `RegroupTicks` (time in exile before refounding), `ExileStartPeasants`,
  `ExileStartWood`/`ExileStartFood` (the starter camp), all in `Exile.cs`.
- Cautious march: `DangerRadius`/`DangerPeak` (`Simulation.cs`) — how far a known
  enemy's avoidance bubble reaches and how strongly it repels the path;
  `RerouteInterval` — how often a cautious unit re-plans against newly-seen danger.
- Units: per-design stats in `Skirmish.Designs()` (Hp/Damage/Speed/Range/Cooldown
  under the `MaxDesignPoints` budget) plus `Sight` (free); `Vision.UnitSight` is the
  default radius. The Scout is design 4 — bump its `Sight`/`SpeedStat` to taste, and
  `StealthDetectRange` (`Simulation.cs`) is how close a watcher must get to spot a
  stealth unit. The Avenger is design 5 (`Trainable = false`, budget-exempt) — tune
  its stats to make the exile deterrent softer or harder.
- Tech: `BaseResearchPace`, `RoadsPace`, `CrossBranchPenalty`, `CapstoneLimit`
  (`Tech.cs`); node costs and the branch shape in `TechTree.cs`.
- Economic income: `TradePostGold`, `MonopolyGold`, `BourseGoldPerBld`,
  `InterestDivisor`/`InterestCap`/`InterestCapGrand`, `GrandExchangeFloor`
  (`Tech.cs`), added to gold each realm tick in `ResolveRealm`.
- Market: `GoodBasePrice` per good and the ±25% spread in `BuyPrice`/`SellPrice`
  (`Market.cs`); `AutoTrade` runs inside `ResolveRealm`. Market build cost in
  `BuildCost`; weapons-arm-recruit branch in the `Train` command (`Simulation.cs`).
- Science: research-pace nodes `LibraryPace`/`ScholarshipPace`/`PrintingPace`
  (`Tech.cs`); Wonder base cost in `BuildCost`, its escalation + Engineering
  discount in `BuildCostFor`; `WonderBuildTime` (the rise + sabotage window,
  `Simulation.cs`); `SciHighWonders`/`SciMedWonders` (`Victory.cs`).
- Domain: `HomesteadMult`, `KeepSpacing`, Keep build cost in `BuildCost`
  (`Simulation.cs`); `DomHighPop`/`DomMedPop`/`DomHighTerr`/`DomMedTerr`
  (`Victory.cs`) — the pop targets are scaled to the prototype (see the census
  note), the territory counts follow the design.
- Conquest: `AnnexRadius` (`Simulation.cs`) — how much population a taken keep
  carries; the `Conquest` node cost in `TechTree.cs`.
- War-tool payoffs: `PrivateerLoot` / `WarLootMat` / `CrusadeFaith` per kill
  (`Tech.cs`); the `Privateers` / `WarLoot` / `Crusade` node costs (`TechTree.cs`).
- Spies: `SpyCost`, `SpyCooldown`, and the per-effect sizes `EmbezzleCap` /
  `InquisitHit`+`InquisitSoft` / `SabotageHit`+`SabotageSoft` / `AgitateHit`+
  `AgitateSoft` (`Spy.cs`); the counters are the branches' own Tier-III nodes.

---

## Balance

`tests/Balance` is a **path-race harness**: it pursues each crown with a lone realm
on the *same* granted build economy (gold and population are NOT granted — they must
be earned and settled), and reports the tick at which each HIGH goal is first
reached. It asserts every path reaches its crown and the spread stays within a
factor, so a retune that breaks parity fails the suite.

The design's headline numbers (a million gold, five thousand souls) assume a far
larger-scale game; at this prototype's rates they'd make Economic and Domain
multi-hour marathons next to a two-minute Religious rush. Scaled to level the field,
the current reach times are:

| Path | Target | Reach |
|---|---|---|
| Science | Academy + 2 wonders | ~1.6 min |
| Religious | 75% faith | ~1.7 min |
| Domain | 180 pop + 5 territories | ~5.8 min |
| Economic | hold 70,000 gold | ~9.6 min |

Spread ~5.9× (was ~395× before tuning). The fast pair (Religious, Science) *reach*
quickly then defend a 10-minute hold against the Inquisitor / Saboteur; the slow
pair (Domain, Economic) build up. Hold windows: Economic 15 min, the rest 10 min.
Re-run `tests/Balance` after any change to a path's engine or target.
