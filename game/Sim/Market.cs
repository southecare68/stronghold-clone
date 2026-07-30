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

        // A ±25% spread around the reference price: you buy above it and sell
        // below, and the gap is the merchant's cut — steep enough that idly
        // churning a good back and forth bleeds gold, so a policy has to price
        // in the spread to pay off.
        public int BuyPrice(int good)  => (GoodBasePrice[good] * 5 + 3) / 4;   // ceil(base * 1.25)
        public int SellPrice(int good) => GoodBasePrice[good] * 3 / 4;         // floor(base * 0.75)

        public int MarketGoodTypes => MarketGoodCount;
        public string GoodName(int good) => GoodNames[good];

        // How much of a good a realm holds, in the good's own stock slot.
        public int GoodStock(int owner, int good) =>
            _stock.TryGetValue(owner, out var s) ? s[GoodSlot[good]] : 0;

        // Arms in the stockpile — bought at a market, spent to recruit soldiers.
        public int Weapons(int owner) => _stock.TryGetValue(owner, out var s) ? s[WeaponsIdx] : 0;

        // A realm can trade once it has raised at least one trading hall.
        public bool HasMarket(int owner) => CountBuildings(owner, BuildingType.Market) > 0;

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
