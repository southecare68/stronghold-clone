using System;

namespace Sim
{
    // The market — a trading hall where a realm turns gold into goods and goods
    // back into gold, the way Stronghold's market does. Owning a live Market
    // building unlocks trading; the market itself has bottomless supply and
    // demand, so a trade always clears at the posted price.
    //
    // Two ways to trade:
    //   • by hand — a Trade command buys or sells a lump this instant.
    //   • on standing orders — a per-good policy (Buy up to N / Sell above N)
    //     that the realm tick settles automatically every turn (AutoTrade),
    //     which is what lets a well-set market run the economy on its own.
    //
    // Everything here mutates the per-owner stock array, so it is part of the
    // deterministic sim and folds into StateChecksum with the rest of _stock.
    public sealed partial class Simulation
    {
        // Auto-trade modes, packed into the low 2 bits of a policy slot; the
        // threshold rides in the bits above (see SetTradePolicy).
        public const int TradeOff = 0, TradeBuy = 1, TradeSell = 2;

        // The five tradeable goods, in market/HUD order. Each maps to a stock
        // slot and carries a reference price; the buy/sell spread is derived
        // from it. Weapons are market-only (no gatherer, no producer) — the one
        // good you can get ONLY by trading, and a barracks arms a recruit from
        // them instead of spending wood (see the Train command).
        static readonly int[] GoodSlot      = { (int)ResourceType.Wood, (int)ResourceType.Stone, (int)ResourceType.Food, (int)ResourceType.Iron, WeaponsIdx };
        static readonly int[] GoodBasePrice = { 4,      5,       3,      12,     30 };
        static readonly string[] GoodNames  = { "Wood", "Stone", "Food", "Iron", "Weapons" };

        // The merchant takes a wide cut: you BUY at +25% over the reference price and
        // SELL at only HALF of it. That steep spread is deliberate — it stops a realm
        // from turning a pile of mined stone or iron into a gold fountain by dumping it
        // on the market, and it makes idly churning a good back and forth bleed gold.
        public int BuyPrice(int good)  => (GoodBasePrice[good] * 5 + 3) / 4;   // ceil(base * 1.25)
        public int SellPrice(int good) => GoodBasePrice[good] / 2;             // half the reference price

        public int MarketGoodTypes => MarketGoodCount;
        public string GoodName(int good) => GoodNames[good];

        // How much of a good a realm holds, in the good's own stock slot.
        public int GoodStock(int owner, int good) =>
            _stock.TryGetValue(owner, out var s) ? s[GoodSlot[good]] : 0;

        // Arms in the stockpile — bought at a market, spent to recruit soldiers.
        public int Weapons(int owner) => _stock.TryGetValue(owner, out var s) ? s[WeaponsIdx] : 0;

        // A realm can trade once it has raised at least one trading hall.
        public bool HasMarket(int owner) => CountBuildings(owner, BuildingType.Market) > 0;

        // --- Mercenaries ------------------------------------------------------
        // The market hires trained soldiers for gold — no peasant, no housing, no
        // muster. That is the point: a rich realm turns its hoard straight into an
        // army, bypassing the population/food gate that bounds a trained one (they
        // still eat their rations once fielded, like any soldier). A premium price
        // over training keeps it a gold SINK, not a shortcut around the economy.
        // The roster maps a barracks design to its hire price; scouts are not for
        // sale (stealth is your own edge to keep).
        static readonly (int Design, int Price)[] MercRoster = { (0, 120), (3, 150), (2, 200) };
        public int MercTypes => MercRoster.Length;
        public int MercDesign(int i) => MercRoster[i].Design;
        public int MercPrice(int i) => MercRoster[i].Price;
        int MercPriceForDesign(int designId)
        {
            foreach (var m in MercRoster) if (m.Design == designId) return m.Price;
            return 0;   // 0 == not a hireable design
        }

        // Hire one mercenary of a design: pay its gold price and muster a trained
        // soldier at the realm's first market. Refused with no market, an unknown
        // design, or too little gold — so an over-eager click simply does nothing.
        void TryHireMercenary(int owner, int designId)
        {
            int price = MercPriceForDesign(designId);
            if (price <= 0) return;
            if (designId >= _designs.Count) return;          // must be a registered design, else you'd pay for one and get another
            var market = FirstBuildingOf(owner, BuildingType.Market);
            if (market == null) return;                      // needs a trading hall
            var s = StockOf(owner);
            if (s[GoldIdx] < price) return;

            var spot = NearestFreeTile(market.CenterX, market.CenterY) ?? new Tile(market.CenterX, market.CenterY);
            var merc = SpawnUnit(owner, spot.X, spot.Y, designId);   // a soldier (non-peasant), fully trained
            merc.IsMercenary = true;                                 // and on the payroll — see PayMercenaryWages
            s[GoldIdx] -= price;
        }

        // The wage a mercenary of a design draws each realm tick — a fraction of its
        // hire price, so dearer troops cost more to keep. At least 1, so every merc
        // costs something to hold.
        const int MercWageDivisor = 50;   // wage/tick ≈ hire price / 50 (Soldier 2, Archer 3, Brute 4)
        int MercWage(int designId) => Math.Max(1, MercPriceForDesign(designId) / MercWageDivisor);

        // For the HUD: how many mercenaries a realm keeps, and the gold-per-realm-tick
        // wage bill they draw — so a player can see the running cost of their company.
        public int MercenaryCount(int owner)
        {
            int n = 0;
            foreach (var u in Units) if (u.Alive && u.Owner == owner && u.IsMercenary) n++;
            return n;
        }
        public int MercenaryWageBill(int owner)
        {
            int bill = 0;
            foreach (var u in Units) if (u.Alive && u.Owner == owner && u.IsMercenary) bill += MercWage(u.DesignId);
            return bill;
        }

        // Pay the realm's mercenaries, oldest first, from whatever gold is on hand.
        // Any the treasury cannot cover DESERT (Hp 0 → swept by RemoveDead), so a
        // gold-bought army is capped at what income sustains — the fairness valve on
        // "more income ⇒ more troops". Deterministic: units in id order, integer math.
        void PayMercenaryWages(int owner, int[] s)
        {
            int budget = s[GoldIdx], paid = 0;
            foreach (var u in Units)      // id order — the longest-serving keep their post
            {
                if (!u.Alive || u.Owner != owner || !u.IsMercenary) continue;
                int wage = MercWage(u.DesignId);
                if (paid + wage <= budget) paid += wage;
                else u.Hp = 0;            // unpaid — the mercenary deserts
            }
            s[GoldIdx] = budget - paid;
        }

        // The owner's first live building of a type, in id order (deterministic).
        Building FirstBuildingOf(int owner, BuildingType type)
        {
            foreach (var b in Buildings) if (b.Alive && b.Owner == owner && b.Type == type) return b;
            return null;
        }

        // A one-shot trade: qty > 0 buys, qty < 0 sells. A buy is capped by the
        // gold on hand; a sell by the goods on hand — so an over-ambitious order
        // simply does as much as it can rather than failing outright.
        void TryTrade(int owner, int good, int qty)
        {
            if (good < 0 || good >= MarketGoodCount || qty == 0) return;
            if (!HasMarket(owner)) return;

            var s = StockOf(owner);
            int slot = GoodSlot[good];
            if (qty > 0)
            {
                int price = BuyPrice(good);
                int n = price > 0 ? Math.Min(qty, s[GoldIdx] / price) : 0;
                if (n <= 0) return;
                s[GoldIdx] -= n * price;
                s[slot]    += n;
            }
            else
            {
                int n = Math.Min(-qty, s[slot]);
                if (n <= 0) return;
                s[slot]    -= n;
                s[GoldIdx] += n * SellPrice(good);
            }
        }

        // Set a good's standing order. `packed` is (threshold << 2 | mode): the
        // HUD builds it, the wire carries it as one int, and it is stored the
        // same way so the whole policy is one deterministic stock slot.
        void SetTradePolicy(int owner, int good, int packed)
        {
            if (good < 0 || good >= MarketGoodCount) return;
            int mode = packed & 3;
            if (mode < TradeOff || mode > TradeSell) mode = TradeOff;
            int threshold = packed >> 2;
            if (threshold < 0) threshold = 0;
            StockOf(owner)[MarketPolicyBase + good] = (threshold << 2) | mode;
        }

        // The current standing order for a good, for the HUD to render and cycle.
        public (int Mode, int Threshold) TradePolicy(int owner, int good)
        {
            if (good < 0 || good >= MarketGoodCount || !_stock.TryGetValue(owner, out var s))
                return (TradeOff, 0);
            int packed = s[MarketPolicyBase + good];
            return (packed & 3, packed >> 2);
        }

        // Settle every good's standing order for one realm, once per realm tick.
        // Buy closes the shortfall up to the threshold, spending as much of the
        // treasury as it takes (capped by gold); Sell dumps everything above the
        // threshold. No market building, or a good left Off, does nothing. Good
        // order is fixed (0..N), so two machines settle identically.
        void AutoTrade(int owner, int[] s)
        {
            if (!HasMarket(owner)) return;
            for (int g = 0; g < MarketGoodCount; g++)
            {
                int packed = s[MarketPolicyBase + g];
                int mode = packed & 3;
                if (mode == TradeOff) continue;
                int threshold = packed >> 2;
                int slot = GoodSlot[g];

                if (mode == TradeBuy)
                {
                    int gap = threshold - s[slot];
                    if (gap <= 0) continue;
                    int price = BuyPrice(g);
                    int n = price > 0 ? Math.Min(gap, s[GoldIdx] / price) : 0;
                    if (n <= 0) continue;
                    s[GoldIdx] -= n * price;
                    s[slot]    += n;
                }
                else // TradeSell
                {
                    int surplus = s[slot] - threshold;
                    if (surplus <= 0) continue;
                    s[slot]    -= surplus;
                    s[GoldIdx] += surplus * SellPrice(g);
                }
            }
        }
    }
}
