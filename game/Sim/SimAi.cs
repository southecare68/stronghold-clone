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
    public sealed partial class Simulation
    {
        const int AiInterval = 12;      // ticks between decisions
        const int AiArmyTarget = 16;    // stop training once the army reaches this
        const int AiAttackAt = 5;       // and march out once it reaches this
        const int AiWorkerReserve = 1;  // idle peasants to keep spare (buildings hire the rest)

        void StepAi()
        {
            if (TickNumber % AiInterval != 0) return;
            foreach (int owner in _aiOwners) StepAiOwner(owner);   // SortedSet: same order everywhere
        }

        void StepAiOwner(int owner)
        {
            var keep = AiKeep(owner);
            if (keep == null) return;    // no keep — the bot has been beaten, nothing to do

            // One considered step per cadence — helpers return true once they have
            // spent on a build, so the bot never empties its purse at once.
            //
            // 1) The self-running food economy comes up FIRST and in strict order:
            //    woodcutter, then farm -> mill -> bakery. The mill and bakery cost
            //    grain and flour the farm has not produced yet, so the bot must WAIT
            //    for them — the `return` after each unbuilt link idles its spare
            //    peasants rather than spending them on expansion or an army. That
            //    reservation is the whole fix: over-building past the four starting
            //    peasants (the earlier bug) left the bakery unmanned, and a broken
            //    chain bakes no bread, breeds no population, and the bot froze at
            //    four peasants forever.
            if (AiCount(owner, BuildingType.WoodcutterHut) < 1 &&
                AiBuildHarvester(owner, keep, BuildingType.WoodcutterHut, ResourceType.Wood)) return;
            if (AiCount(owner, BuildingType.Farm) < 1 && AiBuildWork(owner, keep, BuildingType.Farm)) return;
            if (AiCount(owner, BuildingType.Mill) < 1) { AiBuildWork(owner, keep, BuildingType.Mill); return; }
            if (AiCount(owner, BuildingType.Bakery) < 1) { AiBuildWork(owner, keep, BuildingType.Bakery); return; }

            // 2) With bread flowing, a barracks and housing — neither needs a worker,
            //    so they never starve the economy. House early to give food the room
            //    to breed the peasants that become both workers and soldiers.
            if (AiCount(owner, BuildingType.Barracks) < 1 && AiBuildByKeep(owner, keep, BuildingType.Barracks)) return;
            if (PeasantCount(owner) >= PopulationCap(owner) - 2 &&
                AiBuildByKeep(owner, keep, BuildingType.House)) return;

            // 3) Expansion, once the grown population can spare the hands for it.
            if (AiCount(owner, BuildingType.WoodcutterHut) < 2 &&
                AiBuildHarvester(owner, keep, BuildingType.WoodcutterHut, ResourceType.Wood)) return;
            if (AiCount(owner, BuildingType.Quarry) < 1 &&
                AiBuildHarvester(owner, keep, BuildingType.Quarry, ResourceType.Stone)) return;

            // 4) Arm the surplus peasants, then send the army at the enemy.
            if (AiTryTrain(owner)) return;
            AiCommandArmy(owner);
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
                        if (!ExploredFootprint(owner, type, x, y)) continue;
                        if (AiFootprintHasNode(type, x, y)) continue;   // don't bulldoze deposits
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

        bool AiTryTrain(int owner)
        {
            if (ArmySize(owner) >= AiArmyTarget) return false;
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
        void AiCommandArmy(int owner)
        {
            if (ArmySize(owner) < AiAttackAt) return;   // stay home massing until strong

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
