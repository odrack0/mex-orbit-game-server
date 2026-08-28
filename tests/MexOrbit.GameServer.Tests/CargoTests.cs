// Caracterizacion de la bodega volante y la base: recoger cajas, la frontera del
// rango de la estacion, descargar y vender.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class CargoTests
{
    private const long Asterium = 10;

    /// <summary>Un mundo con una caja en el suelo: se consigue matando al bicho,
    /// que es como se consiguen de verdad.</summary>
    private static (TestWorld M, FakePort P, ulong BoxId) WithBoxOnTheGround(
        uint hold = 300, Dictionary<long, uint>? cargo = null)
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 1, 1, 0)
            .WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
                30, 1, 0, 0, 0, 30, 30))
            .Build();
        var p = m.Enter(1, laserDamage: 100, cargo: cargo, data: m.Pilot(1, hold: hold));
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        var box = p.Last<BoxSpawn>();
        p.Clear();
        return (m, p, box.BoxId);
    }

    // ─── recoleccion ────────────────────────────────────────────────────────

    [Fact]
    public void Collecting_in_range_fills_the_hold_and_records_the_pickup()
    {
        var (m, p, boxId) = WithBoxOnTheGround();

        m.W.Post(new CollectBoxCmd(p, 7, boxId));
        m.Tick();

        var outcome = p.Last<CollectResult>();
        Assert.Equal(7ul, outcome.RequestId);
        Assert.Single(outcome.Drops);
        Assert.Equal("asterium", outcome.Drops[0].MaterialId);
        Assert.Equal(30u, outcome.Drops[0].Amount);
        Assert.Equal(30u, p.Last<HeroStats>().Cargo);

        // la caja queda vacia y desaparece
        Assert.Equal(BoxDespawnReason.Collected, p.Last<BoxDespawn>().Reason);
        Assert.DoesNotContain(boxId, m.W.LiveBoxes);
        Assert.Single(m.Bd.Pickups);
        Assert.Equal((long)boxId, m.Bd.Pickups[0].BoxRef);
    }

    [Fact]
    public void Collecting_from_far_away_answers_TOO_FAR()
    {
        var (m, p, boxId) = WithBoxOnTheGround();
        var (x, y) = m.Ship(1);
        m.MoveTo(p, x + 400, y);      // CollectRange son 250
        p.Clear();

        m.W.Post(new CollectBoxCmd(p, 7, boxId));
        m.Tick();

        var error = p.Last<ErrorReply>();
        Assert.Equal(ErrorCode.TooFar, error.Code);
        Assert.Equal(7ul, error.RequestId);
        Assert.Contains(boxId, m.W.LiveBoxes);
    }

    [Fact]
    public void Collecting_a_box_that_no_longer_exists_answers_GONE()
    {
        var (m, p, _) = WithBoxOnTheGround();

        m.W.Post(new CollectBoxCmd(p, 7, 999_999));
        m.Tick();

        Assert.Equal(ErrorCode.Gone, p.Last<ErrorReply>().Code);
    }

    [Fact]
    public void With_a_full_hold_it_answers_INSUFFICIENT()
    {
        // llega a la caja con la bodega ya al tope: el hueco es 0
        var (m, p, boxId) = WithBoxOnTheGround(
            hold: 300, cargo: new Dictionary<long, uint> { [Asterium] = 300 });

        m.W.Post(new CollectBoxCmd(p, 7, boxId));
        m.Tick();

        Assert.Equal(ErrorCode.Insufficient, p.Last<ErrorReply>().Code);
        Assert.Contains(boxId, m.W.LiveBoxes);
    }

    [Fact]
    public void A_partial_pickup_leaves_the_rest_in_the_box()
    {
        var (m, p, boxId) = WithBoxOnTheGround(hold: 10);

        m.W.Post(new CollectBoxCmd(p, 7, boxId));
        m.Tick();

        Assert.Equal(10u, p.Last<CollectResult>().Drops[0].Amount);
        // la caja sobrevive con las 20 unidades que no cupieron
        Assert.Contains(boxId, m.W.LiveBoxes);
        Assert.Null(p.LastOrNull<BoxDespawn>());
    }

    [Fact]
    public void If_the_db_fails_the_pickup_does_not_lie_to_the_client()
    {
        var (m, p, boxId) = WithBoxOnTheGround();
        m.Bd.FailsOnWrite = true;

        m.W.Post(new CollectBoxCmd(p, 7, boxId));
        m.Tick();

        Assert.Equal(ErrorCode.Generic, p.Last<ErrorReply>().Code);
        Assert.Null(p.LastOrNull<CollectResult>());
        Assert.Empty(m.Bd.Pickups);
    }

    [Fact]
    public void The_box_expires_after_150_seconds()
    {
        var (m, p, boxId) = WithBoxOnTheGround();

        m.Seconds(149);
        Assert.Contains(boxId, m.W.LiveBoxes);

        m.Seconds(3);
        Assert.DoesNotContain(boxId, m.W.LiveBoxes);
        Assert.Equal(BoxDespawnReason.Expired, p.Last<BoxDespawn>().Reason);
    }

    // ─── la base ────────────────────────────────────────────────────────────

    [Fact]
    public void Entering_and_leaving_station_range_notifies_the_client()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500).Build();
        var p = m.Enter(1);   // se entra justo encima de la estacion

        Assert.True(p.Last<StationRange>().InRange);
        Assert.Equal(1ul, p.Last<StationRange>().StationId);

        m.MoveTo(p, 13_000, 6_000);   // fuera del radio de 1500
        Assert.False(p.Last<StationRange>().InRange);

        m.MoveTo(p, 10_500, 6_000);   // de vuelta dentro
        Assert.True(p.Last<StationRange>().InRange);
    }

    [Fact]
    public void Unloading_away_from_the_station_answers_TOO_FAR()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500).Build();
        var p = m.Enter(1);
        m.MoveTo(p, 15_000, 6_000);
        p.Clear();

        m.W.Post(new UnloadCargoCmd(p, 3));
        m.Tick();

        var error = p.Last<ErrorReply>();
        Assert.Equal(ErrorCode.TooFar, error.Code);
        Assert.Equal(3ul, error.RequestId);
    }

    [Fact]
    public void Unloading_at_the_station_returns_what_was_stored_and_refined()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500)
            .WithBias(new MaterialBias(Asterium, "asterium", 1m))
            .WithRecipe(new RefineRecipe(20, "aurorium", 1, new Dictionary<long, uint> { [Asterium] = 30 }))
            .Build();
        var p = m.Enter(1, cargo: new Dictionary<long, uint> { [Asterium] = 30 });
        m.Bd.NextUnload = new UnloadOutcome(
            new Dictionary<long, uint> { [Asterium] = 30 },
            new Dictionary<long, uint> { [20] = 1 });
        p.Clear();

        m.W.Post(new UnloadCargoCmd(p, 3));
        m.Tick();

        var outcome = p.Last<UnloadResult>();
        Assert.Equal(3ul, outcome.RequestId);
        Assert.Equal("asterium", outcome.Stored[0].MaterialId);
        Assert.Equal(30u, outcome.Stored[0].Amount);
        Assert.Equal("aurorium", outcome.Refined[0].MaterialId);
        // la bodega volante queda vacia
        Assert.Equal(0u, p.Last<HeroStats>().Cargo);
    }

    [Fact]
    public void If_the_unload_fails_in_the_db_the_client_gets_the_error_and_keeps_its_cargo()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500).Build();
        var p = m.Enter(1, cargo: new Dictionary<long, uint> { [Asterium] = 30 });
        m.Bd.FailsOnWrite = true;
        p.Clear();

        m.W.Post(new UnloadCargoCmd(p, 3));
        m.Tick();

        Assert.Equal(ErrorCode.Generic, p.Last<ErrorReply>().Code);
        Assert.Null(p.LastOrNull<UnloadResult>());
    }

    // ─── venta al NPC ───────────────────────────────────────────────────────

    [Fact]
    public void Selling_at_the_station_pays_and_updates_the_balance()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500)
            .WithPrices(new NpcPrice(Asterium, "asterium", 5m)).Build();
        var p = m.Enter(1);
        m.Bd.NextSale = (10, 50m, 1_050m);
        p.Clear();

        m.W.Post(new SellToNpcCmd(p, 4, "asterium", 10));
        m.Tick();

        var sale = p.Last<SellResult>();
        Assert.Equal(4ul, sale.RequestId);
        Assert.Equal(50ul, sale.CreditsGained);
        Assert.Equal(1_050ul, sale.NewCredits);
        Assert.Equal(1_050ul, p.Last<HeroStats>().Credits);
    }

    [Fact]
    public void The_npc_does_not_buy_what_is_not_in_its_table()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500)
            .WithPrices(new NpcPrice(Asterium, "asterium", 5m)).Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new SellToNpcCmd(p, 4, "prometium", 10));
        m.Tick();

        Assert.Equal(ErrorCode.Invalid, p.Last<ErrorReply>().Code);
    }

    [Fact]
    public void Selling_with_no_stock_answers_INSUFFICIENT()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500)
            .WithPrices(new NpcPrice(Asterium, "asterium", 5m)).Build();
        var p = m.Enter(1);
        m.Bd.NextSale = (0, 0m, 0m);
        p.Clear();

        m.W.Post(new SellToNpcCmd(p, 4, "asterium", 10));
        m.Tick();

        Assert.Equal(ErrorCode.Insufficient, p.Last<ErrorReply>().Code);
    }

    [Fact]
    public void Selling_away_from_the_station_answers_TOO_FAR()
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500)
            .WithPrices(new NpcPrice(Asterium, "asterium", 5m)).Build();
        var p = m.Enter(1);
        m.MoveTo(p, 15_000, 6_000);
        p.Clear();

        m.W.Post(new SellToNpcCmd(p, 4, "asterium", 10));
        m.Tick();

        Assert.Equal(ErrorCode.TooFar, p.Last<ErrorReply>().Code);
    }
}
