// Caracterizacion de la sesion: entrada unica, la ventana de gracia que hace que
// una caida de socket no cueste la nave, el regreso y el heartbeat.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class SesionTests
{
    private static Mundo Vacio() => new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500);

    [Fact]
    public void Al_entrar_llega_el_mobiliario_del_mapa_y_el_heroe()
    {
        // El MOBILIARIO viaja completo —limites, estacion, portales, precios—
        // porque es del mapa y no de nadie. Las entidades NO: entran por rango,
        // y de eso se ocupa RelevanciaTests.
        var m = Vacio().ConManiqui().Construir();
        var p = m.Entrar(1);

        var entrada = p.Ultimo<EnterMap>();
        Assert.Equal("1-1", entrada.MapCode);
        Assert.Equal(20_800u, entrada.LimitsX);
        Assert.Equal(10_000u, entrada.StationX);
        Assert.Equal(1_500u, entrada.StationRange);
        Assert.Contains(p.Todos<EntitySpawn>(), e => e.EntityId == 1 && e.Kind == EntityKind.Player);
        Assert.True(p.Recibio<NpcPrices>());
        Assert.True(p.Recibio<HeroStats>());
    }

    [Fact]
    public void Los_portales_viajan_completos_en_EnterMap()
    {
        var m = Vacio()
            .ConPortal(new PortalInfo(7, 10_500, 6_000, "1-2", true, 500, 500))
            .Construir();
        var p = m.Entrar(1);

        var portal = Assert.Single(p.Ultimo<EnterMap>().Portals);
        Assert.Equal(7ul, portal.PortalId);
        Assert.Equal("1-2", portal.TargetMapCode);
        Assert.True(portal.IsWorking);
    }

    [Fact]
    public void Una_sesion_nueva_expulsa_a_la_vieja_avisando()
    {
        var m = Vacio().Construir();
        var vieja = m.Entrar(1);
        vieja.Limpiar();

        var nueva = m.Entrar(1);

        Assert.True(vieja.Recibio<SessionReplaced>());   // jamas en silencio
        Assert.True(vieja.Cerrado);
        Assert.True(nueva.Recibio<EnterMap>());
    }

    // ─── la ventana de gracia ───────────────────────────────────────────────

    [Fact]
    public void Una_caida_de_socket_no_saca_la_nave_del_mundo()
    {
        var m = Vacio().Construir();
        var caido = m.Entrar(1);
        var testigo = m.Entrar(2);
        testigo.Limpiar();

        m.W.Post(new LeaveCmd(caido, "DROPPED"));
        m.Segundos(50);

        // 50 s: dentro de los 60 de gracia, la nave sigue ahi para los demas
        Assert.DoesNotContain(testigo.Todos<EntityDespawn>(), e => e.EntityId == 1);
        Assert.Empty(m.Bd.Cerradas);
    }

    [Fact]
    public void Agotada_la_gracia_la_nave_sale_y_la_sesion_se_cierra()
    {
        var m = Vacio().Construir();
        var caido = m.Entrar(1);
        var testigo = m.Entrar(2);
        testigo.Limpiar();

        m.W.Post(new LeaveCmd(caido, "DROPPED"));
        m.Segundos(62);

        Assert.Contains(testigo.Todos<EntityDespawn>(), e => e.EntityId == 1);
        Espera.A(() => m.Bd.Cerradas.Count == 1, "el cierre de sesion");
        Assert.Equal("TIMEOUT", m.Bd.Cerradas[0].Reason);
        Espera.A(() => m.Bd.Guardados.Count > 0, "el guardado al salir");
    }

    [Fact]
    public void Un_logout_explicito_saca_la_nave_de_inmediato()
    {
        var m = Vacio().Construir();
        var quiere = m.Entrar(1);
        var testigo = m.Entrar(2);
        testigo.Limpiar();

        m.W.Post(new LeaveCmd(quiere, "LOGOUT"));
        m.Tick();

        Assert.Contains(testigo.Todos<EntityDespawn>(), e => e.EntityId == 1);
        Assert.True(quiere.Cerrado);
        Espera.A(() => m.Bd.Cerradas.Count == 1, "el cierre de sesion");
        Assert.Equal("LOGOUT", m.Bd.Cerradas[0].Reason);
    }

    // ─── el regreso ─────────────────────────────────────────────────────────

    [Fact]
    public void Volver_dentro_de_la_gracia_recupera_la_misma_nave()
    {
        var m = Vacio().Construir();
        var viejo = m.Entrar(1, carga: new Dictionary<long, uint> { [10] = 42 });
        m.MoverA(viejo, 12_000, 6_000);
        m.W.Post(new LeaveCmd(viejo, "DROPPED"));
        m.Segundos(10);

        var nuevo = new PuertoFalso(1);
        m.W.Post(new ResumeCmd(nuevo, 1, 1, null, 0, 0, null));
        m.Tick();

        Assert.True(nuevo.Recibio<ResumeOk>());
        Assert.True(nuevo.Recibio<EnterMap>());       // re-sincronizacion completa
        // la carga no se toco: es el mismo slot, no uno nuevo
        Assert.Equal(42u, nuevo.Ultimo<HeroStats>().Cargo);
        var heroe = nuevo.Todos<EntitySpawn>().First(e => e.EntityId == 1);
        Assert.Equal(12_000ul, heroe.X);
    }

    [Fact]
    public void Llegar_de_otro_mapa_entra_de_cero_en_vez_de_RESUME_EXPIRED()
    {
        // este mundo no ha visto nunca a esta cuenta: es justo lo que pasa al
        // cruzar un portal, y no es un error
        var m = Vacio().Construir();

        var puerto = new PuertoFalso(9);
        m.W.Post(new ResumeCmd(puerto, 9, 1, m.Piloto(9), 100, 1_000, []));
        m.Tick();

        Assert.True(puerto.Recibio<ResumeOk>());
        Assert.True(puerto.Recibio<EnterMap>());
        Assert.Null(puerto.UltimoOrNull<ErrorReply>());
    }

    [Fact]
    public void Volver_sin_nave_que_reconstruir_expira()
    {
        var m = Vacio().Construir();

        var puerto = new PuertoFalso(9);
        m.W.Post(new ResumeCmd(puerto, 9, 1, null, 0, 0, null));
        m.Tick();

        Assert.Equal(ErrorCode.ResumeExpired, puerto.Ultimo<ErrorReply>().Code);
        Assert.True(puerto.Cerrado);
    }

    // ─── heartbeat ──────────────────────────────────────────────────────────

    [Fact]
    public void El_heartbeat_pregunta_cada_diez_segundos()
    {
        var m = Vacio().ConPing().Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.Segundos(9);
        Assert.Empty(p.Todos<Ping>());

        m.Segundos(2);
        Assert.Single(p.Todos<Ping>());
    }

    [Fact]
    public void Contestar_el_Pong_mantiene_viva_la_sesion()
    {
        var m = Vacio().ConPing().Construir();
        var p = m.Entrar(1);

        for (var vuelta = 0; vuelta < 6; vuelta++)
        {
            m.Segundos(11);
            m.W.Post(new PongCmd(p, p.Ultimo<Ping>().Nonce));
            m.Tick();
        }

        Assert.False(p.Cerrado);
    }

    [Fact]
    public void Tres_pings_sin_respuesta_cierran_el_socket_y_abren_la_gracia()
    {
        var m = Vacio().ConPing().Construir();
        var p = m.Entrar(1);
        var testigo = m.Entrar(2);
        testigo.Limpiar();

        m.Segundos(45);

        Assert.Equal(3, p.Todos<Ping>().Count);
        Assert.True(p.Cerrado);
        // se abre la gracia, NO se pierde la nave
        Assert.DoesNotContain(testigo.Todos<EntityDespawn>(), e => e.EntityId == 1);
        Assert.Empty(m.Bd.Cerradas);
    }

    [Fact]
    public void Un_Pong_con_nonce_equivocado_no_cuenta()
    {
        var m = Vacio().ConPing().Construir();
        var p = m.Entrar(1);

        m.Segundos(45);
        m.W.Post(new PongCmd(p, 999_999));
        m.Tick();

        Assert.True(p.Cerrado);
    }
}
