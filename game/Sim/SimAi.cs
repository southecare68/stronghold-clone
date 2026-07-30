// SimAi.cs — a basic computer opponent, living inside the deterministic sim.
//
// The whole point of putting the AI HERE, rather than in the Godot layer issuing
// lockstep commands like a human, is determinism. StepAi runs inside Tick, on the
// same fog-updated world and at the same point in the sequence on every machine,
// and touches no RNG. So two machines running the same match compute byte-
// identical AI decisions with ZERO network traffic — the bot never puts a command
// on the wire. It is also stateless: every decision is re-derived from the world
// each cadence, so there is nothing extra to snapshot for a rejoiner.
//
// It is deliberately simple — a priority ladder, one action per cadence:
//   raise the economy buildings, keep housing ahead of the population, arm the
//   spare peasants, and once the army is big enough, march it at the enemy keep.
// Everything it does goes through the same Apply() command path a player uses, so
// it obeys the same fog, cost, and placement rules — no cheating.

using System;
using System.Collections.Generic;

namespace Sim
{
    // How tough the computer plays. Easy thinks slowly, keeps a small army, and
    // commits it late; Hard thinks fast, runs a second food chain to grow a much
    // larger population, and fields a big army. Normal sits between.
    public enum AiLevel { Easy = 0, Normal = 1, Hard = 2 }

    public sealed partial class Simulation
    {
        // The dials difficulty turns. Interval is ticks between decisions (lower =
        // acts faster at everything); FoodChains is how many farm->mill->bakery sets
        // it runs (more food -> more population -> more soldiers); Woodcutters and
        // MaxHouses size the rest of the economy; ArmyCap caps recruiting; AttackAt
        // is the army size that triggers the march. BonusPeasants/BonusWood are a
        // start-of-match handicap — a tougher bot begins with more hands and timber
        // so it can staff a second food chain at once and reach a genuinely bigger
        // economy, the standard way an RTS makes an AI harder without cheating in
        // play (it still obeys fog, cost, and placement like anyone else).
        readonly struct AiTuning
        {
            public readonly int Interval, FoodChains, Woodcutters, MaxHouses, ArmyCap, AttackAt, BonusPeasants, BonusWood, BonusFood, Churches;
            public AiTuning(int interval, int foodChains, int woodcutters, int maxHouses, int armyCap, int attackAt, int bonusPeasants, int bonusWood, int bonusFood, int churches)
            {
                Interval = interval; FoodChains = foodChains; Woodcutters = woodcutters; MaxHouses = maxHouses;
                ArmyCap = armyCap; AttackAt = attackAt; BonusPeasants = bonusPeasants; BonusWood = bonusWood; BonusFood = bonusFood; Churches = churches;
            }
        }

        // BonusFood is a starting larder sized to the level's opening mouths: a tougher
        // bot begins with more hands (BonusPeasants), and rations draw the larder down
        // every realm tick, so without a matching stock those extra mouths would starve
        // through the food-chain ramp — popularity would crash and the bigger economy
        // would never actually grow. It feeds the head start until the bakeries bake.
        static AiTuning TuningFor(AiLevel level) => level switch
        {
            //                          interval chains wood houses armyCap attackAt +peas +wood +food churches
            AiLevel.Easy   => new AiTuning(30,     1,    1,    1,      4,      4,      0,     0,     0,    0),
            AiLevel.Hard   => new AiTuning(8,      2,    2,    6,     30,      6,     10,   400,   200,    4),
            _  /* Normal*/ => new AiTuning(12,     1,    2,    3,     12,      5,      4,   150,    60,    2),
        };

        const int AiWorkerReserve = 1;  // idle peasants to keep spare (buildings hire the rest)

        void StepAi()
        {
            foreach (var kv in _aiOwners) StepAiOwner(kv.Key, kv.Value);   // SortedDictionary: same order everywhere
        }

        void StepAiOwner(int owner, AiLevel level)
        {
            var t = TuningFor(level);
            if (TickNumber % t.Interval != 0) return;   // the cadence, per difficulty

            var keep = AiKeep(owner);
            if (keep == null) return;    // no keep — the bot has been beaten, nothing to do

            // Which crown this bot is chasing (default Religious). Research runs
            // alongside the build ladder — it spends banked RESEARCH, its own currency,
            // so it never takes a build slot or starves the economy. A pursuing bot
            // climbs its branch to the capstone that unlocks its crown; Easy abstains,
            // playing a plain economy-and-army game.
            var path = AiPathOf(owner);
            bool pursues = level != AiLevel.Easy;
            if (pursues)
            {
                int rival = FirstRival(owner);
                // Espionage is a Hard-bot craft: only a realm with the food to feast its
                // people (offsetting the funding tax) runs the spy ring. Normal and its
                // war-tool alone; Easy neither.
                bool spymaster = level == AiLevel.Hard;
                // Its own branch first; once that has nothing affordable, a spymaster
                // climbs the Spy Guild toward the dagger that answers its rival's crown.
                if (!AiResearch(owner, PlanFor(path)) && spymaster) AiResearchEspionage(owner, rival);
                // Fund and run espionage (its own currencies — no build slot taken).
                if (spymaster) AiRunEspionage(owner, rival);
            }

            // One considered step per cadence — helpers return true once they have
            // spent on a build, so the bot never empties its purse at once.
            //
            // 1) The FIRST food chain comes up before anything else and in strict
            //    order: woodcutter, then farm -> mill -> bakery. The mill and bakery
            //    cost grain and flour the farm has not produced yet, so the bot must
            //    WAIT for them — the `return` after each unbuilt link idles its spare
            //    peasants rather than spending them on expansion or an army. That
            //    reservation is the whole point: over-building past the four starting
            //    peasants left the bakery unmanned, and a broken chain bakes no
            //    bread, breeds no population, and the bot froze at four peasants.
            if (AiCount(owner, BuildingType.WoodcutterHut) < 1 &&
                AiBuildHarvester(owner, keep, BuildingType.WoodcutterHut, ResourceType.Wood)) return;
            if (AiCount(owner, BuildingType.Farm) < 1 && AiBuildWork(owner, keep, BuildingType.Farm)) return;
            if (AiCount(owner, BuildingType.Mill) < 1) { AiBuildWork(owner, keep, BuildingType.Mill); return; }
            if (AiCount(owner, BuildingType.Bakery) < 1) { AiBuildWork(owner, keep, BuildingType.Bakery); return; }

            // 2) With bread flowing, a barracks and housing — neither needs a worker,
            //    so they never starve the economy. House up to the level's cap to
            //    give food the room to breed the peasants that become both workers
            //    and soldiers; a higher cap is most of why Hard fields a bigger army.
            if (AiCount(owner, BuildingType.Barracks) < 1 && AiBuildByKeep(owner, keep, BuildingType.Barracks)) return;
            if (AiCount(owner, BuildingType.House) < t.MaxHouses &&
                PeasantCount(owner) >= PopulationCap(owner) - 2 &&
                AiBuildByKeep(owner, keep, BuildingType.House)) return;

            // 3) Expansion, once the grown population can spare the hands for it:
            //    extra food chains (each a farm -> mill -> bakery set, gated so a
            //    link is only raised when there is a peasant to run it), more
            //    woodcutters, and a quarry.
            if (AiCount(owner, BuildingType.Farm) < t.FoodChains && AiBuildWork(owner, keep, BuildingType.Farm)) return;
            if (AiCount(owner, BuildingType.Mill) < t.FoodChains && AiBuildWork(owner, keep, BuildingType.Mill)) return;
            if (AiCount(owner, BuildingType.Bakery) < t.FoodChains && AiBuildWork(owner, keep, BuildingType.Bakery)) return;
            if (AiCount(owner, BuildingType.WoodcutterHut) < t.Woodcutters &&
                AiBuildHarvester(owner, keep, BuildingType.WoodcutterHut, ResourceType.Wood)) return;
            if (AiCount(owner, BuildingType.Quarry) < 1 &&
                AiBuildHarvester(owner, keep, BuildingType.Quarry, ResourceType.Stone)) return;

            // 3b) The victory-path investment, out here once the economy stands: the
            //     churches, wonders, or new keeps its crown wants (Easy builds none).
            //     Whatever it cannot yet afford simply falls through to the army below,
            //     so this never deadlocks — a working army is always the fallback.
            if (pursues && AiBuildForPath(owner, keep, path, t)) return;

            // 4) Arm the surplus peasants up to the cap, then send the army out.
            if (AiTryTrain(owner, t.ArmyCap)) return;
            AiCommandArmy(owner, t.AttackAt);
        }

        Building AiKeep(int owner)
        {
            foreach (var b in Buildings)   // id order
                if (b.Alive && b.Owner == owner && b.Type == BuildingType.Keep) return b;
            return null;
        }

        int AiCount(int owner, BuildingType type)
        {
            int n = 0;
            foreach (var b in Buildings)
                if (b.Alive && b.Owner == owner && b.Type == type) n++;
            return n;
        }

        // Each branch from the trunk to its capstone, in dependency order (taking the
        // first fork option). The bot researches the first node it can afford each
        // cadence, climbing steadily toward the crown-unlocking capstone.
        // ...to the capstone, then the branch's ⚔ war-tool, so every fight the bot
        // picks feeds its crown (Conquest even annexes the keeps it takes).
        static readonly int[] AiEconomicPlan  = { TechTree.Roads, TechTree.Market, TechTree.TradePost, TechTree.Monopoly, TechTree.BankingHouse, TechTree.GrandExchange, TechTree.Privateers };
        static readonly int[] AiReligiousPlan = { TechTree.Roads, TechTree.Chapel, TechTree.Shrine, TechTree.Missionaries, TechTree.Cathedral, TechTree.GrandTemple, TechTree.Crusade };
        static readonly int[] AiSciencePlan   = { TechTree.Roads, TechTree.ScholarsHut, TechTree.Library, TechTree.Scholarship, TechTree.PrintingPress, TechTree.Academy, TechTree.WarLoot };
        static readonly int[] AiDomainPlan    = { TechTree.Roads, TechTree.Farmstead, TechTree.Husbandry, TechTree.Homesteads, TechTree.ProvincialKeeps, TechTree.SovereignsCourt, TechTree.Conquest };

        static int[] PlanFor(VictoryPath path) => path switch
        {
            VictoryPath.Economic => AiEconomicPlan,
            VictoryPath.Science => AiSciencePlan,
            VictoryPath.Domain => AiDomainPlan,
            _ => AiReligiousPlan,
        };

        bool AiResearch(int owner, int[] plan)
        {
            foreach (int id in plan)
                if (TryResearch(owner, id)) return true;
            return false;
        }

        // The dagger that answers a rival's leading crown (its highest HIGH-progress
        // path). A shared tool, so this rides no cross-branch penalty.
        static int AiSpyForPath(VictoryPath p) => p switch
        {
            VictoryPath.Economic => TechTree.Embezzler,
            VictoryPath.Religious => TechTree.Inquisitor,
            VictoryPath.Science => TechTree.Saboteur,
            _ => TechTree.Agitator,
        };

        int AiRivalSpy(int rival)
        {
            VictoryPath lead = VictoryPath.Economic;
            int best = -1;
            for (int p = 0; p < PathCount; p++)
            {
                int pct = Progress(rival, (VictoryPath)p).HighPercent;
                if (pct > best) { best = pct; lead = (VictoryPath)p; }
            }
            return AiSpyForPath(lead);
        }

        // Climb the spy branch: Muster, the Spy Guild, then the rival's dagger.
        void AiResearchEspionage(int owner, int rival)
        {
            if (TryResearch(owner, TechTree.Muster)) return;
            if (TryResearch(owner, TechTree.SpyGuild)) return;
            if (rival >= 0) TryResearch(owner, AiRivalSpy(rival));
        }

        // Fund espionage with a fair tax the bot keeps level by feasting its people
        // (set once), then loose a ready dagger at the rival whenever it can pay.
        void AiRunEspionage(int owner, int rival)
        {
            var s = StockOf(owner);
            if (s[TaxIdx] < 4) { s[TaxIdx] = 4; s[RationIdx] = 4; }   // Fair tax (+gold), Hearty table (offsets the approval)
            if (rival < 0) return;
            int spy = AiRivalSpy(rival);
            if (IsTechResearched(owner, spy) && CanSpy(owner, spy, rival)) TrySpy(owner, spy, rival);
        }

        // The path-specific investment, once the economy stands. Returns true when it
        // spends a build. Religious raises churches; Science raises wonders (once the
        // Academy stands, and only when it can afford the escalating cost); Domain
        // houses its census and founds new keeps; Economic hoards the trade income and
        // builds nothing extra.
        bool AiBuildForPath(int owner, Building keep, VictoryPath path, AiTuning t)
        {
            switch (path)
            {
                case VictoryPath.Religious:
                    if (AiCount(owner, BuildingType.Church) < t.Churches)
                        return AiBuildByKeep(owner, keep, BuildingType.Church);
                    return false;

                case VictoryPath.Science:
                    if (IsTechResearched(owner, TechTree.Academy) && AiCount(owner, BuildingType.Wonder) < 2
                        && CanAfford(owner, BuildCostFor(owner, BuildingType.Wonder)))
                        return AiTryBuild(owner, BuildingType.Wonder, keep.CenterX, keep.CenterY, 16);
                    return false;

                case VictoryPath.Domain:
                    // Room for the census first — houses past the base cap while the
                    // population presses against it — then found new territories.
                    if (AiCount(owner, BuildingType.House) < t.MaxHouses + 6
                        && PeasantCount(owner) >= PopulationCap(owner) - 4)
                        return AiBuildByKeep(owner, keep, BuildingType.House);
                    if (TerritoryCount(owner) < 5) return AiFoundKeep(owner, keep);
                    return false;

                default:   // Economic — the branch's trade income is the whole engine
                    return false;
            }
        }

        // Found a new keep — a new territory — spaced clear of the bot's other keeps,
        // once Provincial Keeps is researched and the stone is in hand. Scans outward
        // from the home keep starting beyond the spacing radius, so the first hit is
        // the nearest legal site; the Build command re-checks everything.
        bool AiFoundKeep(int owner, Building home)
        {
            if (!IsTechResearched(owner, TechTree.ProvincialKeeps)) return false;
            if (!CanAfford(owner, BuildCost[(int)BuildingType.Keep])) return false;
            for (int r = KeepSpacing; r <= KeepSpacing + 24; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = home.CenterX + dx, y = home.CenterY + dy;
                        if (!CanPlace(BuildingType.Keep, x, y)) continue;
                        if (!KeepFarEnough(owner, x, y)) continue;
                        if (!BuildableFootprint(owner, BuildingType.Keep, x, y)) continue;
                        if (AiFootprintHasNode(BuildingType.Keep, x, y)) continue;
                        Apply(new Command { Owner = owner, Type = CommandType.Build, TargetId = (int)BuildingType.Keep, X = x, Y = y });
                        return true;
                    }
            return false;
        }

        // Raise a harvester (hut/quarry) beside its resource. Placed a few tiles off
        // the cluster toward the keep so it neighbours the deposit without being
        // dropped on top of it (which would clear the very nodes it works). Gated on
        // a spare peasant, so the bot never raises a hut it cannot staff.
        bool AiBuildHarvester(int owner, Building keep, BuildingType type, ResourceType res)
        {
            if (IdlePeasantCount(owner) < 1) return false;
            if (!CanAfford(owner, BuildCost[(int)type])) return false;
            var node = AiNearestExploredNode(owner, res, keep.CenterX, keep.CenterY);
            if (node == null) return false;   // none found yet (e.g. deposit still in fog)
            int sx = node.X, sy = node.Y;
            StepToward(ref sx, ref sy, keep.CenterX, keep.CenterY, 3);
            return AiTryBuild(owner, type, sx, sy, 8);
        }

        // A workshop/farm near the keep (they pull from the shared stockpile or sow
        // their own field, so they need no deposit) — but they DO need a worker, so
        // likewise gated on a spare peasant.
        bool AiBuildWork(int owner, Building keep, BuildingType type)
        {
            if (IdlePeasantCount(owner) < 1) return false;
            if (!CanAfford(owner, BuildCost[(int)type])) return false;
            return AiTryBuild(owner, type, keep.CenterX, keep.CenterY, 12);
        }

        // A workerless building near the keep (barracks, house) — safe to raise any
        // time it is wanted and affordable, since it takes no peasant off the economy.
        bool AiBuildByKeep(int owner, Building keep, BuildingType type)
        {
            if (!CanAfford(owner, BuildCost[(int)type])) return false;
            return AiTryBuild(owner, type, keep.CenterX, keep.CenterY, 12);
        }

        // Search rings outward from (cx,cy) for the nearest tile where this building
        // fits, on ground we have explored, clear of resource nodes. The first hit
        // (nearest, then in a fixed scan order) is built through the normal command
        // path, so it is charged and validated exactly like a human's.
        bool AiTryBuild(int owner, BuildingType type, int cx, int cy, int maxR)
        {
            for (int r = 1; r <= maxR; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;   // ring perimeter only
                        int x = cx + dx, y = cy + dy;
                        if (!CanPlace(type, x, y)) continue;
                        if (!BuildableFootprint(owner, type, x, y)) continue;
                        if (AiFootprintHasNode(type, x, y)) continue;   // don't bulldoze deposits
                        // Never build over your own drop-off tile: a building there
                        // walls the delivery point off and every hauler jams up full.
                        if (_dropOff.TryGetValue(owner, out var drop))
                        {
                            var (dw, dh) = FootprintOf(type);
                            if (drop.X >= x && drop.X < x + dw && drop.Y >= y && drop.Y < y + dh) continue;
                        }
                        // A farm on barren ground would yield nothing, so the bot only
                        // sites one where its field will find fertile soil to grow on.
                        if (type == BuildingType.Farm && RequireFertileSoil)
                        {
                            var (fw, fh) = FootprintOf(type);
                            if (!FarmWouldYield(x, y, fw, fh)) continue;
                        }
                        Apply(new Command { Owner = owner, Type = CommandType.Build, TargetId = (int)type, X = x, Y = y });
                        return true;
                    }
            return false;
        }

        bool AiFootprintHasNode(BuildingType type, int x, int y)
        {
            var (w, h) = FootprintOf(type);
            foreach (var n in Nodes)
                if (n.Amount > 0 && n.X >= x && n.X < x + w && n.Y >= y && n.Y < y + h) return true;
            return false;
        }

        ResourceNode AiNearestExploredNode(int owner, ResourceType res, int fromX, int fromY)
        {
            ResourceNode best = null;
            long bestD = long.MaxValue;
            foreach (var n in Nodes)   // id order; ties fall to the lower id
            {
                if (n.Type != res || n.Amount <= 0 || !Map.Passable(n.X, n.Y)) continue;
                if (!HasExplored(owner, n.X, n.Y)) continue;
                long ddx = n.X - fromX, ddy = n.Y - fromY;
                long d = ddx * ddx + ddy * ddy;
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }

        // Nudge (x,y) up to `steps` tiles toward (tx,ty), one tile per axis per step.
        static void StepToward(ref int x, ref int y, int tx, int ty, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                if (x < tx) x++; else if (x > tx) x--;
                if (y < ty) y++; else if (y > ty) y--;
            }
        }

        bool AiTryTrain(int owner, int armyCap)
        {
            if (ArmySize(owner) >= armyCap) return false;
            if (IdlePeasantCount(owner) < AiWorkerReserve + 1) return false;   // keep the workforce
            if (!CanAfford(owner, new[] { TrainCostWood, 0, 0 })) return false;
            Building barracks = null;
            foreach (var b in Buildings)   // lowest-id barracks, for a stable choice
                if (b.Alive && b.Owner == owner && b.Type == BuildingType.Barracks) { barracks = b; break; }
            if (barracks == null) return false;
            Apply(new Command { Owner = owner, Type = CommandType.Train, TargetId = barracks.Id, X = 0 });
            return true;
        }

        // March the army at the enemy: attack a foe we can see, else besiege the
        // enemy keep once we have scouted it, else advance on it to reveal the way.
        // Only units with no current order are (re)directed, so a soldier already
        // locked in a fight is left to it — combat chains its own next target.
        void AiCommandArmy(int owner, int attackAt)
        {
            if (ArmySize(owner) < attackAt) return;   // stay home massing until strong

            var idle = new List<int>();
            foreach (var u in Units)   // id order
                if (!u.IsPeasant && u.Owner == owner && u.Alive &&
                    u.GarrisonId == 0 && u.TargetId == 0 && u.TargetBuildingId == 0)
                    idle.Add(u.Id);
            if (idle.Count == 0) return;

            var ids = idle.ToArray();
            var foe = AiNearestVisibleEnemy(owner);
            if (foe != null)
            {
                Apply(new Command { Owner = owner, Type = CommandType.Attack, UnitIds = ids, TargetId = foe.Id });
                return;
            }

            Building enemyKeep = null;
            foreach (var b in Buildings)
                if (b.Alive && b.Owner != owner && b.Type == BuildingType.Keep) { enemyKeep = b; break; }
            if (enemyKeep == null) return;

            if (HasExploredBuilding(owner, enemyKeep))
            {
                Apply(new Command { Owner = owner, Type = CommandType.AttackBuilding, UnitIds = ids, TargetId = enemyKeep.Id });
            }
            else
            {
                // Advance on the enemy keep to scout it — but march to a PASSABLE
                // tile beside it, never its own footprint, or A* finds no path to the
                // blocked centre and the army just stands at home.
                var approach = AiPassableNear(enemyKeep.CenterX, enemyKeep.CenterY);
                Apply(new Command { Owner = owner, Type = CommandType.Move, UnitIds = ids, X = approach.X, Y = approach.Y });
            }
        }

        // The nearest passable tile to (cx,cy), spiralling out. Used to aim a march
        // at a building without pathing onto its blocked footprint.
        Sim.Tile AiPassableNear(int cx, int cy)
        {
            if (Map.Passable(cx, cy)) return new Tile(cx, cy);
            for (int r = 1; r <= 12; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = cx + dx, y = cy + dy;
                        if (Map.Passable(x, y)) return new Tile(x, y);
                    }
            return new Tile(cx, cy);   // nothing nearby is passable; let A* refuse it
        }

        Unit AiNearestVisibleEnemy(int owner)
        {
            var keep = AiKeep(owner);
            int fx = keep?.CenterX ?? 0, fy = keep?.CenterY ?? 0;
            Unit best = null;
            long bestD = long.MaxValue;
            foreach (var u in Units)   // id order; ties to lower id
            {
                if (!u.Alive || u.Owner == owner || !CanSeeUnit(owner, u)) continue;
                long dx = Fixed.ToInt(u.X) - fx, dy = Fixed.ToInt(u.Y) - fy;
                long d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = u; }
            }
            return best;
        }
    }
}
