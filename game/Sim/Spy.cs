using System;

namespace Sim
{
    // The spy counter-web (spy.pdf). Each spy is the dedicated answer to one crown —
    // the ONLY thing that pushes a rival's announced metric backward — plus the
    // Assassin against whoever leans hardest on force. A spy is trained by research
    // (its War-branch node), costs gold and sits on a cooldown, and its bite is
    // blunted by the target's own Tier-III counter (the opportunity cost that makes
    // being targeted survivable). All deterministic and integer, applied in-tick, so
    // both clients raise the same daggers.
    public sealed partial class Simulation
    {
        const int SpyCost = 80;              // gold per operation
        const int SpyCooldown = 200;         // ticks between uses of the same spy (10s)
        // Effect sizes, and the softened size when the target holds the counter.
        const int EmbezzleCap = 500;         // gold skimmed (up to a quarter of the hoard)
        const int InquisitHit = 20, InquisitSoft = 8;      // faith points knocked off
        // Damage to a wonder (500 hp). A wonder-holder necessarily has Printing Press
        // (the Academy's prereq), so a Saboteur is always at the softened figure —
        // two determined operations wreck a wonder, one Sealed-Archives detour aside.
        const int SabotageHit = 500, SabotageSoft = 280;
        const int AgitateHit = 3, AgitateSoft = 1;         // peasants driven to emigrate

        // Spy node → cooldown slot (0..4), in the order of SpyReadyBase.
        public static readonly int[] SpyNodes =
            { TechTree.Embezzler, TechTree.Inquisitor, TechTree.Saboteur, TechTree.Agitator, TechTree.Assassin };
        static int SpyIndex(int node) => Array.IndexOf(SpyNodes, node);

        // The tick a spy is next usable for this owner (0 = ready from the start).
        public int SpyReadyAt(int owner, int spyNode)
        {
            int idx = SpyIndex(spyNode);
            if (idx < 0 || !_stock.TryGetValue(owner, out var s)) return 0;
            return s[SpyReadyBase + idx];
        }
        // Ticks until a spy can be used again — 0 when ready. For the HUD.
        public int SpyReadyIn(int owner, int spyNode) => Math.Max(0, SpyReadyAt(owner, spyNode) - TickNumber);

        // The first rival realm (a keep-holder that isn't you), in owner order — the
        // default target when there is a single obvious enemy.
        public int FirstRival(int owner)
        {
            var rivals = new System.Collections.Generic.SortedSet<int>();
            foreach (var b in Buildings) if (b.Alive && b.Type == BuildingType.Keep && b.Owner != owner) rivals.Add(b.Owner);
            foreach (int r in rivals) return r;
            return -1;
        }

        // May this owner run this spy against this target right now?
        public bool CanSpy(int owner, int spyNode, int target)
        {
            if (SpyIndex(spyNode) < 0) return false;
            if (!IsTechResearched(owner, spyNode)) return false;     // untrained
            if (target == owner || TerritoryCount(target) <= 0) return false;   // needs a real rival realm
            if (Gold(owner) < SpyCost) return false;
            return TickNumber >= SpyReadyAt(owner, spyNode);          // off cooldown
        }

        // Run the operation if it is legal: charge the gold, start the cooldown, land
        // the effect. The command path ignores the result; tests and the HUD read it.
        public bool TrySpy(int owner, int spyNode, int target)
        {
            if (!CanSpy(owner, spyNode, target)) return false;
            var s = StockOf(owner);
            s[GoldIdx] = Math.Max(0, s[GoldIdx] - SpyCost);
            s[SpyReadyBase + SpyIndex(spyNode)] = TickNumber + SpyCooldown;
            ApplySpy(owner, spyNode, target);
            return true;
        }

        void ApplySpy(int owner, int spyNode, int target)
        {
            switch (spyNode)
            {
                case TechTree.Embezzler:   // → Economic: skim the hoard into yours (Banking House / Vault resists)
                {
                    int gold = Gold(target);
                    int steal = Math.Min(gold / 4, EmbezzleCap);
                    if (IsTechResearched(target, TechTree.BankingHouse)) steal /= 3;
                    StockOf(target)[GoldIdx] = Math.Max(0, gold - steal);
                    StockOf(owner)[GoldIdx] = Gold(owner) + steal;      // the loot funds YOUR path
                    break;
                }
                case TechTree.Inquisitor:  // → Religious: push conversion backward (Cathedral / Inquisition resists)
                {
                    int hit = IsTechResearched(target, TechTree.Cathedral) ? InquisitSoft : InquisitHit;
                    var t = StockOf(target);
                    t[FaithIdx] = Math.Max(0, t[FaithIdx] - hit);
                    break;
                }
                case TechTree.Saboteur:    // → Science: wreck a wonder (Printing Press / Sealed Archives resists)
                {
                    int dmg = IsTechResearched(target, TechTree.PrintingPress) ? SabotageSoft : SabotageHit;
                    Building wonder = null;   // the newest standing wonder
                    foreach (var b in Buildings)
                        if (b.Alive && b.Owner == target && b.Type == BuildingType.Wonder && (wonder == null || b.Id > wonder.Id))
                            wonder = b;
                    if (wonder != null)
                    {
                        wonder.Hp -= dmg;
                        if (wonder.Hp <= 0) { TearDownBuilding(wonder); Buildings.Remove(wonder); }
                    }
                    break;
                }
                case TechTree.Agitator:    // → Domain: incite emigration (Provincial Keeps / Festival Hall resists)
                {
                    int n = IsTechResearched(target, TechTree.ProvincialKeeps) ? AgitateSoft : AgitateHit;
                    for (int i = 0; i < n; i++) EmigrateOnePeasant(target);
                    break;
                }
                case TechTree.Assassin:    // → the war tool: cut down a soldier (Bodyguard blocks it outright)
                {
                    if (IsTechResearched(target, TechTree.Bodyguard)) break;
                    foreach (var u in Units)
                        if (!u.IsPeasant && u.Owner == target && u.Alive) { u.Hp = 0; break; }   // swept by the dead-unit pass
                    break;
                }
            }
        }
    }
}
