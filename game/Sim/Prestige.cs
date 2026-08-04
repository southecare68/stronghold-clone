// Prestige — the court's renown. A power currency (not a victory track): earned by
// winning battles and at the Royal Kitchen's feasts & tournaments, spent to raise
// Statues (which then pay it back forever) and to muster Champions. Deterministic
// like everything in the sim — it rides the per-owner stock array, so it is hashed
// and snapshotted exactly like gold or faith, and travels the wire untouched.

using System;

namespace Sim
{
    public partial class Simulation
    {
        // ── Earning ──────────────────────────────────────────────────────────────
        const int GloryPerKill = 3;        // base renown for felling a foe...
        const int GloryRankBonus = 2;      // ...plus this per rank the SLAYER holds (a champion's kills sing louder)
        const int FeastFood = 4;           // food a Royal Kitchen spends each cycle to feast
        const int FeastPrestige = 2;       // renown a feast yields
        const int FeastPopularity = 1;     // and the goodwill of a generous table
        const int TourneyEvery = 6;        // a grand tournament every N realm cycles
        const int TourneyGold = 12;        // gold a tournament spends
        const int TourneyPrestige = 8;     // its burst of renown
        const int TourneyPopularity = 3;   // and the crowd's delight
        const int StatuePrestige = 1;      // renown each standing Statue radiates per cycle — the compounding loop

        // ── Spending ─────────────────────────────────────────────────────────────
        public const int StatueCost = 20;    // Prestige to raise a Statue (on top of its stone)
        public const int ChampionCost = 40;  // Prestige to muster a Champion
        public const int DragonCost = 120;   // Prestige to muster a Dragon — a legend, dear as three Champions

        public int Prestige(int owner) => _stock.TryGetValue(owner, out var s) ? s[PrestigeIdx] : 0;
        void AddPrestige(int owner, int amount)
        {
            var s = StockOf(owner);
            s[PrestigeIdx] = Math.Max(0, s[PrestigeIdx] + amount);
        }

        // Renown for a kill — called from RegisterKill, so every felled foe adds to the
        // slayer's court, scaled by the rank they've earned.
        void AwardGlory(Unit killer) => AddPrestige(killer.Owner, GloryPerKill + GloryRankBonus * RankOf(killer));

        // The court economy, on the realm cadence (aligned with ResolveRealm, and
        // skipped while paused since it runs inside the world update). Each keep-holding
        // realm's Royal Kitchen feasts (food → renown + goodwill) and, now and then,
        // throws a tournament (gold → a burst); every Statue quietly compounds.
        void ResolvePrestige()
        {
            if (TickNumber == 0 || TickNumber % RealmInterval != 0) return;
            bool tourneyCycle = (TickNumber / RealmInterval) % TourneyEvery == 0;

            foreach (var b in Buildings)                 // id order
            {
                if (!b.Alive || !b.Complete) continue;
                if (b.Type == BuildingType.RoyalKitchen)
                {
                    var s = StockOf(b.Owner);
                    if (s[(int)ResourceType.Food] >= FeastFood)
                    {
                        s[(int)ResourceType.Food] -= FeastFood;
                        s[PrestigeIdx] += FeastPrestige;
                        s[PopIdx] = Math.Clamp(s[PopIdx] + FeastPopularity, 0, 100);
                    }
                    if (tourneyCycle && s[GoldIdx] >= TourneyGold)
                    {
                        s[GoldIdx] -= TourneyGold;
                        s[PrestigeIdx] += TourneyPrestige;
                        s[PopIdx] = Math.Clamp(s[PopIdx] + TourneyPopularity, 0, 100);
                    }
                }
                else if (b.Type == BuildingType.Statue)
                {
                    StockOf(b.Owner)[PrestigeIdx] += StatuePrestige;
                }
            }
        }

        // The Champion design (registered by Skirmish): the LAST non-trainable, non-siege,
        // non-FLYING design that isn't the Avenger. -1 if none is registered (a bare sim),
        // so a muster fails gracefully rather than raising the wrong unit. The Flying
        // exclusion is what keeps the Dragon (also non-trainable) from being mustered here.
        int ChampionDesign()
        {
            int avenger = AvengerDesign();
            for (int i = DesignList.Count - 1; i >= 0; i--)
                if (!DesignList[i].Trainable && !DesignList[i].IsSiege && !DesignList[i].Flying && i != avenger) return i;
            return -1;
        }

        // The Dragon design (registered by Skirmish): the flying legend. -1 if none.
        int DragonDesign()
        {
            for (int i = DesignList.Count - 1; i >= 0; i--)
                if (DesignList[i].Flying) return i;
            return -1;
        }

        public bool CanMusterChampion(int owner) =>
            ChampionDesign() >= 0 && Prestige(owner) >= ChampionCost && HasRoyalKitchen(owner);

        public bool CanMusterDragon(int owner) =>
            DragonDesign() >= 0 && Prestige(owner) >= DragonCost && HasRoyalKitchen(owner);

        bool HasRoyalKitchen(int owner)
        {
            foreach (var b in Buildings)
                if (b.Alive && b.Complete && b.Owner == owner && b.Type == BuildingType.RoyalKitchen) return true;
            return false;
        }

        // Muster a Champion: needs a standing Royal Kitchen and the Prestige to pay. It
        // rises beside the court, exactly as the Avenger rises beside a fallen keep.
        void TryMusterChampion(int owner)
        {
            if (!CanMusterChampion(owner)) return;
            Building court = null;
            foreach (var b in Buildings)
                if (b.Alive && b.Complete && b.Owner == owner && b.Type == BuildingType.RoyalKitchen) { court = b; break; }
            if (court == null) return;
            AddPrestige(owner, -ChampionCost);
            var at = NearestFreeTile(court.CenterX, court.CenterY) ?? new Tile(court.CenterX, court.CenterY);
            SpawnUnit(owner, at.X, at.Y, ChampionDesign());
        }

        // Muster a Dragon: needs a standing Royal Kitchen and a king's ransom in Prestige.
        // The legend rises beside the court, as the Champion does — but flying and
        // fire-breathing, answerable only to another Dragon or a Harpoon Tower.
        void TryMusterDragon(int owner)
        {
            if (!CanMusterDragon(owner)) return;
            Building court = null;
            foreach (var b in Buildings)
                if (b.Alive && b.Complete && b.Owner == owner && b.Type == BuildingType.RoyalKitchen) { court = b; break; }
            if (court == null) return;
            AddPrestige(owner, -DragonCost);
            var at = NearestFreeTile(court.CenterX, court.CenterY) ?? new Tile(court.CenterX, court.CenterY);
            SpawnUnit(owner, at.X, at.Y, DragonDesign());
        }
    }
}
