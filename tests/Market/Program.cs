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
        HiringAMercenaryMustersASoldierForGold();
        HiringNeedsAMarketAndEnoughGold();
        OnlyRosteredDesignsCanBeHired();
        AnAutoEconomyCanFieldAnArmyOfMercenaries();
        MercenariesDrawWagesEachTurn();
        OverhiringBeyondIncomeDeserts();
        TwoClientsAgreeOnTrading();
        TwoClientsAgreeOnHiring();

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

    // Mercenaries: gold buys a trained soldier outright — no peasant, no barracks,
    // no muster — the whole point being it bypasses the population gate.
    static void HiringAMercenaryMustersASoldierForGold()
    {
        Console.WriteLine("\nhiring a mercenary musters a soldier for gold:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddGold(1, 500);
        int price = sim.MercPrice(0);                       // the first merc on the roster
        int design = sim.MercDesign(0);
        int before = sim.Units.Count;

        Order(sim, Hire(1, design));
        Check($"gold paid the hire price ({price})", sim.Gold(1) == 500 - price);
        Check("a unit mustered", sim.Units.Count == before + 1);
        var merc = sim.Units[sim.Units.Count - 1];
        Check("the merc is a fighting soldier, not a peasant", !merc.IsPeasant && merc.Owner == 1);
        Check("and no peasant was spent (none even existed)", sim.PeasantCount(1) == 0);
    }

    // The two gates on hiring: you need a trading hall, and you need the gold.
    static void HiringNeedsAMarketAndEnoughGold()
    {
        Console.WriteLine("\nhiring needs a market and enough gold:");
        var noMarket = new Simulation(TileMap.Open(48));
        noMarket.AddGold(1, 500);
        Order(noMarket, Hire(1, noMarket.MercDesign(0)));
        Check("no market → no mercenary, gold untouched", noMarket.Units.Count == 0 && noMarket.Gold(1) == 500);

        var poor = new Simulation(TileMap.Open(48));
        poor.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        poor.AddGold(1, poor.MercPrice(0) - 1);            // a coin short
        Order(poor, Hire(1, poor.MercDesign(0)));
        Check("too little gold → no mercenary", poor.Units.Count == 0);
    }

    // Only the rostered combat designs are for hire — not, say, the stealth Scout.
    static void OnlyRosteredDesignsCanBeHired()
    {
        Console.WriteLine("\nonly rostered designs can be hired:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddGold(1, 5000);
        Order(sim, Hire(1, 99));                            // a design that isn't on the roster
        Check("an off-roster design hires nothing", sim.Units.Count == 0 && sim.Gold(1) == 5000);
    }

    // The showcase: a rich, hands-off market economy turns gold straight into an
    // army the population could never have raised in the time.
    static void AnAutoEconomyCanFieldAnArmyOfMercenaries()
    {
        Console.WriteLine("\nan auto economy fields an army of mercenaries:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddGold(1, 2000);
        int hired = 0;
        for (int i = 0; i < 6; i++) { Order(sim, Hire(1, sim.MercDesign(0))); if (sim.Units.Count == hired + 1) hired++; }
        Check("gold raised a whole company with no peasants", hired >= 6 && sim.PeasantCount(1) == 0);
        Check("and the treasury was drawn down for it", sim.Gold(1) == 2000 - 6 * sim.MercPrice(0));
    }

    // The fairness valve: a standing mercenary draws wages from the treasury every
    // realm tick, so an army is a running cost, not a one-off — and that cost eats
    // the very hoard a gold economy is racing to grow.
    static void MercenariesDrawWagesEachTurn()
    {
        Console.WriteLine("\nmercenaries draw wages each turn:");
        var sim = new Simulation(TileMap.Open(48));
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);       // a realm, so wages are settled
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        sim.AddGold(1, 500);
        Order(sim, Hire(1, sim.MercDesign(0)));              // hire a Soldier (120g)
        Check("one merc on the payroll", MercCount(sim, 1) == 1);

        int afterHire = sim.Gold(1);
        Settle(sim, 45);                                     // one realm tick (40)
        Check("wages drew the treasury down", sim.Gold(1) < afterHire);
        Check("but the merc stays while it can be paid", MercCount(sim, 1) == 1);
    }

    // Over-hire beyond what income sustains and the treasury can't cover the wage
    // bill — the mercs it cannot pay desert, capping the army at sustainable size.
    static void OverhiringBeyondIncomeDeserts()
    {
        Console.WriteLine("\nmercenaries you can't pay desert:");
        var sim = new Simulation(TileMap.Open(48));
        foreach (var d in Skirmish.Designs()) sim.RegisterDesign(d);   // so the Brute design exists to hire
        sim.PlaceBuilding(BuildingType.Keep, 1, 2, 2);
        sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
        int price = sim.MercPrice(2);                        // Brute 200g, wage 4/turn
        sim.AddGold(1, price * 5 + 10);                      // enough to HIRE five, barely any left to keep them
        for (int i = 0; i < 5; i++) Order(sim, Hire(1, sim.MercDesign(2)));
        Check("five mercs hired", MercCount(sim, 1) == 5);
        Check("with the treasury nearly bare", sim.Gold(1) == 10);

        Settle(sim, 40);                                     // the first payday
        Check("the mercs it couldn't pay deserted", MercCount(sim, 1) < 5);
        Check("it keeps only the two the 10 gold could cover", MercCount(sim, 1) == 2);
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

    // Hiring spawns units, so two machines must agree on the roster mustered and
    // where each merc lands — the sternest determinism test of the feature.
    static void TwoClientsAgreeOnHiring()
    {
        Console.WriteLine("\ntwo clients agree on hiring mercenaries:");
        var net = new LoopbackTransport();
        var a = new Client(1, net, TileMap.Open(64));
        var b = new Client(2, net, TileMap.Open(64));
        net.Connect(a);
        net.Connect(b);
        foreach (var c in new[] { a, b })
        {
            foreach (var d in Skirmish.Designs()) c.Sim.RegisterDesign(d);   // the full roster, so all merc types exist
            c.Sim.PlaceBuilding(BuildingType.Market, 1, 20, 20);
            c.Sim.AddGold(1, 3000);
        }

        int desyncs = 0, first = -1;
        for (int t = 0; t < 400; t++)
        {
            if (t == 10) { a.Issue(Hire(1, a.Sim.MercDesign(0))); b.Issue(Hire(1, b.Sim.MercDesign(0))); }
            if (t == 20) { a.Issue(Hire(1, a.Sim.MercDesign(2))); b.Issue(Hire(1, b.Sim.MercDesign(2))); }
            if (t == 30) { a.Issue(Hire(1, a.Sim.MercDesign(1))); b.Issue(Hire(1, b.Sim.MercDesign(1))); }
            a.SendInput(); b.SendInput();
            a.TryStep();   b.TryStep();
            if (a.Sim.StateChecksum() != b.Sim.StateChecksum()) { if (first < 0) first = t; desyncs++; }
        }
        Check($"StateChecksum identical on all 400 ticks" + (desyncs > 0 ? $" (diverged {desyncs}x, first at {first})" : ""), desyncs == 0);
        Check($"and the company mustered on both ({a.Sim.Units.Count} units)", a.Sim.Units.Count == 3 && b.Sim.Units.Count == 3);
    }

    // ---- helpers -----------------------------------------------------------

    static int BarracksId(Simulation sim)
    {
        foreach (var b in sim.BuildingList) if (b.Type == BuildingType.Barracks) return b.Id;
        return 0;
    }

    static int MercCount(Simulation sim, int owner)
    {
        int n = 0;
        foreach (var u in sim.Units) if (u.Alive && u.Owner == owner && u.IsMercenary) n++;
        return n;
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
    static Command Hire(int owner, int design) =>
        new Command { Owner = owner, Type = CommandType.HireMercenary, X = design };

    static void Check(string what, bool ok)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    }
}
