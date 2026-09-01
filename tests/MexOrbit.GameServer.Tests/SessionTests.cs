// Caracterizacion de la sesion: entrada unica, la ventana de gracia que hace que
// una caida de socket no cueste la nave, el regreso y el heartbeat.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class SessionTests
{
    private static TestWorld Empty() => new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500);

    [Fact]
    public void On_entering_the_map_furniture_and_the_hero_arrive()
    {
        // El MOBILIARIO viaja completo —limites, estacion, portales, precios—
        // porque es del mapa y no de nadie. Las entidades NO: entran por rango,
        // y de eso se ocupa RelevanciaTests.
        var m = Empty().WithDummy().Build();
        var p = m.Enter(1);

        var entrada = p.Last<EnterMap>();
        Assert.Equal("1-1", entrada.MapCode);
        Assert.Equal(20_800u, entrada.LimitsX);
        Assert.Equal(10_000u, entrada.StationX);
        Assert.Equal(1_500u, entrada.StationRange);
        Assert.Contains(p.All<EntitySpawn>(), e => e.EntityId == 1 && e.Kind == EntityKind.Player);
        Assert.True(p.Received<NpcPrices>());
        Assert.True(p.Received<HeroStats>());
    }

    [Fact]
    public void Portals_travel_complete_in_EnterMap()
    {
        var m = Empty()
            .WithPortal(new PortalInfo(7, 10_500, 6_000, "1-2", true, 500, 500))
            .Build();
        var p = m.Enter(1);

        var portal = Assert.Single(p.Last<EnterMap>().Portals);
        Assert.Equal(7ul, portal.PortalId);
        Assert.Equal("1-2", portal.TargetMapCode);
        Assert.True(portal.IsWorking);
    }

    [Fact]
    public void A_new_session_evicts_the_old_one_with_notice()
    {
        var m = Empty().Build();
        var vieja = m.Enter(1);
        vieja.Clear();

        var nueva = m.Enter(1);

        Assert.True(vieja.Received<SessionReplaced>());   // jamas en silencio
        Assert.True(vieja.Closed);
        Assert.True(nueva.Received<EnterMap>());
    }

    // ─── la ventana de gracia ───────────────────────────────────────────────

    [Fact]
    public void A_dropped_socket_does_not_remove_the_ship_from_the_world()
    {
        var m = Empty().Build();
        var caido = m.Enter(1);
        var testigo = m.Enter(2);
        testigo.Clear();

        m.W.Post(new LeaveCmd(caido, "DROPPED"));
        m.Seconds(50);

        // 50 s: dentro de los 60 de gracia, la nave sigue ahi para los demas
        Assert.DoesNotContain(testigo.All<EntityDespawn>(), e => e.EntityId == 1);
        Assert.Empty(m.Bd.ClosedSessions);
    }

    [Fact]
    public void Once_grace_runs_out_the_ship_leaves_and_the_session_closes()
    {
        var m = Empty().Build();
        var caido = m.Enter(1);
        var testigo = m.Enter(2);
        testigo.Clear();

        m.W.Post(new LeaveCmd(caido, "DROPPED"));
        m.Seconds(62);

        Assert.Contains(testigo.All<EntityDespawn>(), e => e.EntityId == 1);
        Wait.A(() => m.Bd.ClosedSessions.Count == 1, "el cierre de sesion");
        Assert.Equal("TIMEOUT", m.Bd.ClosedSessions[0].Reason);
        Wait.A(() => m.Bd.SavedStates.Count > 0, "el guardado al salir");
    }

    [Fact]
    public void An_explicit_logout_removes_the_ship_at_once()
    {
        var m = Empty().Build();
        var quiere = m.Enter(1);
        var testigo = m.Enter(2);
        testigo.Clear();

        m.W.Post(new LeaveCmd(quiere, "LOGOUT"));
        m.Tick();

        Assert.Contains(testigo.All<EntityDespawn>(), e => e.EntityId == 1);
        Assert.True(quiere.Closed);
        Wait.A(() => m.Bd.ClosedSessions.Count == 1, "el cierre de sesion");
        Assert.Equal("LOGOUT", m.Bd.ClosedSessions[0].Reason);
    }

    // ─── el regreso ─────────────────────────────────────────────────────────

    [Fact]
    public void Returning_within_grace_recovers_the_same_ship()
    {
        var m = Empty().Build();
        var viejo = m.Enter(1, cargo: new Dictionary<long, uint> { [10] = 42 });
        m.MoveTo(viejo, 12_000, 6_000);
        m.W.Post(new LeaveCmd(viejo, "DROPPED"));
        m.Seconds(10);

        var nuevo = new FakePort(1);
        m.W.Post(new ResumeCmd(nuevo, 1, 1, null, 0, 0, null));
        m.Tick();

        Assert.True(nuevo.Received<ResumeOk>());
        Assert.True(nuevo.Received<EnterMap>());       // re-sincronizacion completa
        // la carga no se toco: es el mismo slot, no uno nuevo
        Assert.Equal(42u, nuevo.Last<HeroStats>().Cargo);
        var heroe = nuevo.All<EntitySpawn>().First(e => e.EntityId == 1);
        Assert.Equal(12_000L, heroe.X);
    }

    [Fact]
    public void Arriving_from_another_map_joins_fresh_instead_of_RESUME_EXPIRED()
    {
        // este mundo no ha visto nunca a esta cuenta: es justo lo que pasa al
        // cruzar un portal, y no es un error
        var m = Empty().Build();

        var port = new FakePort(9);
        m.W.Post(new ResumeCmd(port, 9, 1, m.Pilot(9), 100, 1_000, []));
        m.Tick();

        Assert.True(port.Received<ResumeOk>());
        Assert.True(port.Received<EnterMap>());
        Assert.Null(port.LastOrNull<ErrorReply>());
    }

    [Fact]
    public void Returning_with_no_ship_to_rebuild_expires()
    {
        var m = Empty().Build();

        var port = new FakePort(9);
        m.W.Post(new ResumeCmd(port, 9, 1, null, 0, 0, null));
        m.Tick();

        Assert.Equal(ErrorCode.ResumeExpired, port.Last<ErrorReply>().Code);
        Assert.True(port.Closed);
    }

    // ─── heartbeat ──────────────────────────────────────────────────────────

    [Fact]
    public void The_heartbeat_asks_every_ten_seconds()
    {
        var m = Empty().WithPing().Build();
        var p = m.Enter(1);
        p.Clear();

        m.Seconds(9);
        Assert.Empty(p.All<Ping>());

        m.Seconds(2);
        Assert.Single(p.All<Ping>());
    }

    [Fact]
    public void Answering_the_Pong_keeps_the_session_alive()
    {
        var m = Empty().WithPing().Build();
        var p = m.Enter(1);

        for (var vuelta = 0; vuelta < 6; vuelta++)
        {
            m.Seconds(11);
            m.W.Post(new PongCmd(p, p.Last<Ping>().Nonce));
            m.Tick();
        }

        Assert.False(p.Closed);
    }

    [Fact]
    public void Three_unanswered_pings_close_the_socket_and_open_grace()
    {
        var m = Empty().WithPing().Build();
        var p = m.Enter(1);
        var testigo = m.Enter(2);
        testigo.Clear();

        m.Seconds(45);

        Assert.Equal(3, p.All<Ping>().Count);
        Assert.True(p.Closed);
        // se abre la gracia, NO se pierde la nave
        Assert.DoesNotContain(testigo.All<EntityDespawn>(), e => e.EntityId == 1);
        Assert.Empty(m.Bd.ClosedSessions);
    }

    [Fact]
    public void A_Pong_with_the_wrong_nonce_does_not_count()
    {
        var m = Empty().WithPing().Build();
        var p = m.Enter(1);

        m.Seconds(45);
        m.W.Post(new PongCmd(p, 999_999));
        m.Tick();

        Assert.True(p.Closed);
    }
}
