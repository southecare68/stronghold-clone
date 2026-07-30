// Market — buying and selling goods for gold, by hand and on standing orders.
//
// A realm with a trading hall turns gold into goods and back again at a posted
// price with a ±25% spread. On top of one-shot trades, each good can carry a
// standing order — Buy up to N, or Sell above N — that the realm tick settles
// on its own, which is what lets a well-set market run the economy hands-off.
// Weapons are a market-only good that arms recruits in place of wood.
//
// As with every economy test the failure that bites hardest is a determinism
// one, so the twin-client check is the point; the rest pins down the prices,
// the caps, the spread, the auto-trader, and the weapons→recruit coupling.

using System;
using System.Collections.Generic;
using Sim;

static class Program
{
    static int _failures;

    // Market good indices (not ResourceType) — the order the market lists them.
    const int GWood = 0, GStone = 1, GFood = 2, GIron = 3, GWeapons = 4;

    static void Main()
    {
        Console.WriteLine("Market — buy, sell, and auto-trade\n");

        NoMarketNoTrade();
        BuyingSpendsGoldAndStocksTheGood();
        SellingDumpsTheGoodForGold();
        BuyingIsCappedByGold();
        SellingIsCappedByStock();
        TheSpreadCostsYouOnARoundTrip();
        WeaponsAreMarketOnlyAndArmRecruits();
        WithNoWeaponsRecruitsStillCostWood();
        APolicyRoundTripsThroughPackedStorage();
        AutoBuyFillsUpToItsThreshold();
        AutoSellDumpsTheSurplus();
        AutoTradeNeedsAMarket();
        AnAutoBoughtArmouryRefillsItself();
        TwoClientsAgreeOnTrading();

        Console.WriteLine(_failures == 0 ? "\nPASS" : $"\nFAIL — {_failures} check(s) failed");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // Opt-in: no trading hall, no trade. A Trade command from a realm with no
    // market is simply ignored — gold and goods sit untouched.
    static void NoMarketNoTrade()
    {
        Console.WriteLine("no market, no trade:");
        var sim = new Simulation(TileMap.Open(48));
        sim.AddGold(1, 500);
        Order(sim, Buy(1, GWood, 10));
        Check("gold untouched with no market", sim.Gold(1) == 500);
        Check("no wood appeared", sim.Stockpile(1, ResourceType.Wood) == 0);
    }

    // A buy stocks the good and debits gold at the posted buy price.
    static void BuyingSpendsGoldAndStocksTheGood()
    {
        Console.WriteLine("\nbuying stocks the good and spends gold:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddGold(1, 500);
        int price = sim.BuyPrice(GWood);
        Order(sim, Buy(1, GWood, 10));
        Check($"10 wood on hand (buy price {price})", sim.Stockpile(1, ResourceType.Wood) == 10);
        Check($"gold fell by 10×{price}", sim.Gold(1) == 500 - 10 * price);
    }

    // A sell empties the good and credits gold at the (lower) sell price.
    static void SellingDumpsTheGoodForGold()
    {
        Console.WriteLine("\nselling dumps the good for gold:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddResource(1, ResourceType.Stone, 40);
        int price = sim.SellPrice(GStone);
        Order(sim, Sell(1, GStone, 25));
        Check("25 stone left the stockpile", sim.Stockpile(1, ResourceType.Stone) == 15);
        Check($"gold rose by 25×{price}", sim.Gold(1) == 25 * price);
    }

    // You cannot buy on credit — an order larger than the treasury buys only what
    // the gold covers, and never drives gold negative.
    static void BuyingIsCappedByGold()
    {
        Console.WriteLine("\nbuying is capped by gold:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        int price = sim.BuyPrice(GIron);        // 15
        sim.AddGold(1, price * 3 + 7);          // enough for 3, with change left over
        Order(sim, Buy(1, GIron, 100));
        Check("bought only what gold covered (3 iron)", sim.Stockpile(1, ResourceType.Iron) == 3);
        Check("the change stayed in the treasury", sim.Gold(1) == 7);
    }

    // Selling more than you hold sells the lot and no more.
    static void SellingIsCappedByStock()
    {
        Console.WriteLine("\nselling is capped by stock:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddResource(1, ResourceType.Food, 12);
        Order(sim, Sell(1, GFood, 1000));
        Check("only the 12 food on hand sold", sim.Stockpile(1, ResourceType.Food) == 0);
        Check("gold is 12× the sell price", sim.Gold(1) == 12 * sim.SellPrice(GFood));
    }

    // The spread is real: buy a good and sell it straight back and you are down
    // the merchant's cut, so churning is a loss, not free arbitrage.
    static void TheSpreadCostsYouOnARoundTrip()
    {
        Console.WriteLine("\nthe spread costs you on a round trip:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddGold(1, 1000);
        Order(sim, Buy(1, GIron, 10));
        Order(sim, Sell(1, GIron, 10));
        Check("back to zero iron", sim.Stockpile(1, ResourceType.Iron) == 0);
        Check($"and down on gold ({sim.Gold(1)} < 1000)", sim.Gold(1) < 1000);
        Check("the loss is exactly the spread",
              sim.Gold(1) == 1000 - 10 * (sim.BuyPrice(GIron) - sim.SellPrice(GIron)));
    }

    // Weapons come only from the market, and a barracks arms a recruit from a
    // stocked weapon instead of spending wood — the alternate army input.
    static void WeaponsAreMarketOnlyAndArmRecruits()
    {
        Console.WriteLine("\nweapons are market-only and arm recruits:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        var barracks = sim.PlaceBuilding(BuildingType.Barracks, 1, 6, 6);
        for (int i = 0; i < 3; i++) sim.SpawnPeasant(1);
        sim.AddGold(1, 500);
        Order(sim, Buy(1, GWeapons, 2));
        Check("2 weapons bought", sim.Weapons(1) == 2);

        int woodBefore = sim.Stockpile(1, ResourceType.Wood);   // 0 — none gathered
        Order(sim, Train(1, barracks.Id));
        Check("a weapon armed the recruit", sim.Weapons(1) == 1);
        Check("no wood was spent", sim.Stockpile(1, ResourceType.Wood) == woodBefore);
    }

    // The regression guard on the coupling: with no weapons in stock, recruiting
    // spends wood exactly as it always has. This is every match that never trades.
    static void WithNoWeaponsRecruitsStillCostWood()
    {
        Console.WriteLine("\nwith no weapons, recruits still cost wood:");
        var sim = new Simulation(TileMap.Open(48));
        var barracks = sim.PlaceBuilding(BuildingType.Barracks, 1, 6, 6);
        for (int i = 0; i < 3; i++) sim.SpawnPeasant(1);
        sim.AddResource(1, ResourceType.Wood, 100);
        Order(sim, Train(1, barracks.Id));
        Check("wood paid for the recruit (100 → 85)", sim.Stockpile(1, ResourceType.Wood) == 85);
        Check("weapons untouched at zero", sim.Weapons(1) == 0);
    }

    // A standing order survives the round trip through its packed stock slot.
    static void APolicyRoundTripsThroughPackedStorage()
    {
        Console.WriteLine("\na policy round-trips through packed storage:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        Order(sim, Policy(1, GFood, Simulation.TradeSell, 250));
        var (mode, threshold) = sim.TradePolicy(1, GFood);
        Check("mode came back as Sell", mode == Simulation.TradeSell);
        Check("threshold came back as 250", threshold == 250);
    }

    // The auto-trader buys the shortfall up to the threshold each realm tick, then
    // holds — it never overshoots.
    static void AutoBuyFillsUpToItsThreshold()
    {
        Console.WriteLine("\nauto-buy fills up to its threshold:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);      // a realm the tick will settle
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddGold(1, 1000);
        Order(sim, Policy(1, GWood, Simulation.TradeBuy, 50));

        Settle(sim, 60);                                     // past the first realm tick (40)
        int price = sim.BuyPrice(GWood);
        Check("stock climbed to the threshold", sim.Stockpile(1, ResourceType.Wood) == 50);
        Check($"gold fell by 50×{price}", sim.Gold(1) == 1000 - 50 * price);

        int goldAfterFill = sim.Gold(1);
        Settle(sim, 120);                                    // several more realm ticks
        Check("it does not overshoot the threshold", sim.Stockpile(1, ResourceType.Wood) == 50);
        Check("and spends nothing once full", sim.Gold(1) == goldAfterFill);
    }

    // The auto-trader dumps everything above the threshold and settles there.
    static void AutoSellDumpsTheSurplus()
    {
        Console.WriteLine("\nauto-sell dumps the surplus:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddResource(1, ResourceType.Iron, 100);
        Order(sim, Policy(1, GIron, Simulation.TradeSell, 30));

        Settle(sim, 60);
        Check("stock settled at the threshold", sim.Stockpile(1, ResourceType.Iron) == 30);
        Check($"the 70 surplus sold for gold", sim.Gold(1) == 70 * sim.SellPrice(GIron));
    }

    // A policy with no trading hall does nothing — the auto-trader needs a market.
    static void AutoTradeNeedsAMarket()
    {
        Console.WriteLine("\nauto-trade needs a market:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        sim.AddGold(1, 1000);
        Order(sim, Policy(1, GWood, Simulation.TradeBuy, 50));
        Settle(sim, 120);
        Check("no wood bought without a market", sim.Stockpile(1, ResourceType.Wood) == 0);
        Check("gold untouched", sim.Gold(1) == 1000);
    }

    // The centrepiece: a standing weapons order refills itself. It fills to the
    // cap, the barracks draws it down to arm recruits, and the next realm tick tops
    // it back up — a hands-off arms pipeline, and never a scrap of wood spent.
    static void AnAutoBoughtArmouryRefillsItself()
    {
        Console.WriteLine("\nan auto-bought armoury refills itself:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        var barracks = sim.PlaceBuilding(BuildingType.Barracks, 1, 6, 6);
        for (int i = 0; i < 8; i++) sim.SpawnPeasant(1);
        sim.AddResource(1, ResourceType.Food, 600);
        sim.AddGold(1, 3000);
        Order(sim, Policy(1, GWeapons, Simulation.TradeBuy, 10));

        Settle(sim, 60);
        Check("the armoury filled to its cap", sim.Weapons(1) == 10);

        for (int i = 0; i < 4; i++) Order(sim, Train(1, barracks.Id));   // arm four recruits
        Check("four weapons were drawn to arm recruits", sim.Weapons(1) == 6);
        Check("not one scrap of wood was spent", sim.Stockpile(1, ResourceType.Wood) == 0);

        Settle(sim, 60);                                                  // next realm tick tops up
        Check("the standing order refilled the armoury", sim.Weapons(1) == 10);
    }

    // The one that matters most: two clients issuing the same trades and standing
    // orders must agree on every tick — the goods, the gold, the auto-trader.
    static void TwoClientsAgreeOnTrading()
    {
        Console.WriteLine("\ntwo clients agree on trading:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(64));
        var b = new Client(2, net, TileMap.Open(64));
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            c.Sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
            c.Sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
            c.Sim.PlaceBuilding(BuildingType.Barracks, 1, 6, 6);
            for (int i = 0; i < 6; i++) c.Sim.SpawnPeasant(1);
            c.Sim.AddResource(1, ResourceType.Food, 400);
            c.Sim.AddGold(1, 4000);
        }

        int desyncs = 0, first = -1;
        for (int t = 0; t < 600; t++)
        {
            // A scripted mix of hand trades and standing orders over the run.
            if (t == 5)  a.Issue(Policy(1, GWeapons, Simulation.TradeBuy, 12));
            if (t == 5)  b.Issue(Policy(1, GWeapons, Simulation.TradeBuy, 12));
            if (t == 30) { a.Issue(Buy(1, GStone, 40)); b.Issue(Buy(1, GStone, 40)); }
            if (t == 90) { a.Issue(Policy(1, GStone, Simulation.TradeSell, 10)); b.Issue(Policy(1, GStone, Simulation.TradeSell, 10)); }
            if (t == 120) { a.Issue(Train(1, BarracksId(a.Sim))); b.Issue(Train(1, BarracksId(b.Sim))); }

            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 600 ticks" +
              (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check($"and the armoury actually filled ({a.Sim.Weapons(1)} weapons on A)", a.Sim.Weapons(1) > 0);
    }

    // ---- helpers -----------------------------------------------------------

    static int BarracksId(Simulation sim)
    {
        foreach (var b in sim.BuildingList) if (b.Type == BuildingType.Barracks) return b.Id;
        return 0;
    }

    static void Settle(Simulation sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Tick(new List<Command>());
    }

    static void Order(Simulation sim, Command cmd) => sim.Tick(new List<Command> { cmd });

    static Command Buy(int owner, int good, int qty)  => new Command { Owner = owner, Type = CommandType.Trade, X = good, Y = qty };
    static Command Sell(int owner, int good, int qty) => new Command { Owner = owner, Type = CommandType.Trade, X = good, Y = -qty };
    static Command Policy(int owner, int good, int mode, int threshold) =>
        new Command { Owner = owner, Type = CommandType.SetTradePolicy, X = good, Y = (threshold << 2) | mode };
    static Command Train(int owner, int barracksId) =>
        new Command { Owner = owner, Type = CommandType.Train, TargetId = barracksId, X = 0 };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
