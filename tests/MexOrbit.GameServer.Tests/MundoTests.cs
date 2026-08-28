// Caracterizacion del resto del mundo: movimiento autoritativo, salto de sector,
// chat, muerte del jugador y la promesa de que ningun fallo tumba el loop.
using MexOrbit.GameServer.Data;
using MexOrbit.GameServer.Game;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class MovimientoTests
{
    private static Mundo Vacio() => new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500);

    [Fact]
    public void El_destino_se_recorta_a_los_limites_del_mapa()
    {
        var m = Vacio().Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.W.Post(new MoveIntentCmd(p, new MoveIntent { Seq = 1, TargetX = 999_999, TargetY = 999_999 }));
        m.Tick();

        // el Moving eterno del legado, imposible: el clamp es del servidor
        var eco = p.Ultimo<EntityMove>();
        Assert.Equal(20_800ul, eco.TargetX);
        Assert.Equal(12_800ul, eco.TargetY);
    }

    [Fact]
    public void Una_intencion_vieja_se_descarta_sin_drama()
    {
        var m = Vacio().Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.W.Post(new MoveIntentCmd(p, new MoveIntent { Seq = 5, TargetX = 11_000, TargetY = 6_000 }));
        m.W.Post(new MoveIntentCmd(p, new MoveIntent { Seq = 3, TargetX = 9_000, TargetY = 6_000 }));
        m.Tick();

        var ecos = p.Todos<EntityMove>().Where(e => e.EntityId == 1).ToList();
        Assert.Single(ecos);
        Assert.Equal(11_000ul, ecos[0].TargetX);
    }

    [Fact]
    public void El_eco_autoritativo_llega_a_todos_incluido_el_heroe()
    {
        var m = Vacio().Construir();
        var heroe = m.Entrar(1);
        var otro = m.Entrar(2);
        heroe.Limpiar();
        otro.Limpiar();

        m.W.Post(new MoveIntentCmd(heroe, new MoveIntent { Seq = 1, TargetX = 11_000, TargetY = 6_000 }));
        m.Tick();

        Assert.Contains(heroe.Todos<EntityMove>(), e => e.EntityId == 1);
        Assert.Contains(otro.Todos<EntityMove>(), e => e.EntityId == 1);
    }
}

public class SaltoTests
{
    private static Mundo ConPortal(bool funciona = true) =>
        new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500)
            .ConPortal(new PortalInfo(7, 10_500, 6_000, "1-2", funciona, 500, 500));

    [Fact]
    public void Junto_a_un_portal_valido_el_salto_se_negocia_arriba()
    {
        var m = ConPortal().Construir();
        (long Cuenta, PortalInfo Portal)? pedido = null;
        m.W.Saltar += (_, cuenta, portal) => pedido = (cuenta, portal);
        var p = m.Entrar(1);   // entra en 10000,6000: a 500 del portal

        m.W.Post(new JumpCmd(p, 9, 7));
        m.Tick();

        Assert.NotNull(pedido);
        Assert.Equal(1L, pedido!.Value.Cuenta);
        Assert.Equal("1-2", pedido.Value.Portal.TargetMapCode);
    }

    [Fact]
    public void Un_portal_que_no_existe_en_este_mapa_responde_GONE()
    {
        var m = ConPortal().Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.W.Post(new JumpCmd(p, 9, 999));
        m.Tick();

        var error = p.Ultimo<ErrorReply>();
        Assert.Equal(ErrorCode.Gone, error.Code);
        Assert.Equal(9ul, error.RequestId);
    }

    [Fact]
    public void Un_portal_inactivo_responde_INVALID()
    {
        var m = ConPortal(funciona: false).Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.W.Post(new JumpCmd(p, 9, 7));
        m.Tick();

        Assert.Equal(ErrorCode.Invalid, p.Ultimo<ErrorReply>().Code);
    }

    [Fact]
    public void Lejos_del_portal_responde_TOO_FAR_aunque_el_cliente_insista()
    {
        var m = ConPortal().Construir();
        var p = m.Entrar(1);
        m.MoverA(p, 12_000, 6_000);   // JumpRange son 600
        p.Limpiar();

        m.W.Post(new JumpCmd(p, 9, 7));
        m.Tick();

        // el cliente propone, el server dispone (y el cliente puede mentir)
        Assert.Equal(ErrorCode.TooFar, p.Ultimo<ErrorReply>().Code);
    }
}

public class ChatTests
{
    private static Mundo Vacio() => new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500);

    [Fact]
    public void El_canal_global_llega_a_todos_incluido_quien_habla()
    {
        var m = Vacio().Construir();
        var uno = m.Entrar(1, datos: m.Piloto(1, "Ana", faccion: 1));
        var dos = m.Entrar(2, datos: m.Piloto(2, "Beto", faccion: 2));
        uno.Limpiar();
        dos.Limpiar();

        m.W.Post(new ChatSendCmd(uno, 1, ChatChannel.Global, "hola sector"));
        m.Tick();

        Assert.Equal("hola sector", uno.Ultimo<ChatMessage>().Text);
        Assert.Equal("Ana", dos.Ultimo<ChatMessage>().FromName);
    }

    [Fact]
    public void El_canal_de_faccion_no_cruza_facciones()
    {
        var m = Vacio().Construir();
        var uno = m.Entrar(1, datos: m.Piloto(1, "Ana", faccion: 1));
        var mismo = m.Entrar(2, datos: m.Piloto(2, "Ada", faccion: 1));
        var otro = m.Entrar(3, datos: m.Piloto(3, "Beto", faccion: 2));
        mismo.Limpiar();
        otro.Limpiar();

        m.W.Post(new ChatSendCmd(uno, 1, ChatChannel.Faction, "solo los nuestros"));
        m.Tick();

        Assert.True(mismo.Recibio<ChatMessage>());
        Assert.False(otro.Recibio<ChatMessage>());
    }

    [Fact]
    public void Un_mensaje_largo_se_recorta_a_256()
    {
        var m = Vacio().Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.W.Post(new ChatSendCmd(p, 1, ChatChannel.Global, new string('x', 400)));
        m.Tick();

        Assert.Equal(256, p.Ultimo<ChatMessage>().Text.Length);
    }

    [Fact]
    public void El_mensaje_vacio_no_viaja()
    {
        var m = Vacio().Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.W.Post(new ChatSendCmd(p, 1, ChatChannel.Global, "   "));
        m.Tick();

        Assert.False(p.Recibio<ChatMessage>());
    }
}

public class MuerteDelJugadorTests
{
    private static (Mundo M, PuertoFalso P, Entity Npc) Acorralado(
        Dictionary<long, uint>? carga = null)
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 100, 100, 50)
            .ConNpc(new NpcSpawnInfo(1, "ferox", "Ferox", 100_000, 0, 0, 100, true, 0, 2_000,
                30, 1, 0, 0, 0, 0, 0))
            .Construir();
        var p = m.Entrar(1, escudo: 0, carga: carga, datos: m.Piloto(1, hp: 150));
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        return (m, p, npc);
    }

    [Fact]
    public void Al_caer_se_anuncia_la_destruccion_y_se_ofrece_reaparicion()
    {
        // sin Limpiar: el bicho ya dispara mientras el jugador se acerca, asi que
        // la muerte puede caer antes de que la prueba llegue a mirar
        var (m, p, npc) = Acorralado();

        m.Segundos(5);

        var muerte = p.Ultimo<EntityDestroyed>();
        Assert.Equal(1ul, muerte.EntityId);
        Assert.Equal(npc.Id, muerte.KillerId);
        Assert.Equal(0u, p.Ultimo<HeroStats>().Hp);

        var opciones = p.Ultimo<RespawnOptions>();
        Assert.Equal(DeathCause.Npc, opciones.Cause);
        Assert.Equal("Ferox", opciones.KillerName);
        var unica = Assert.Single(opciones.Options);
        Assert.Equal(1ul, unica.OptionId);
        Assert.Equal(0ul, unica.CostCredits);
        Assert.True(unica.Available);
    }

    [Fact]
    public void La_bodega_volante_se_queda_en_el_sitio_dentro_de_una_caja()
    {
        var (m, p, _) = Acorralado(new Dictionary<long, uint> { [10] = 42 });

        m.Segundos(5);

        // transferencia, no destruccion (guidelines §7)
        Assert.True(p.Recibio<BoxSpawn>());
        Espera.A(() => m.Bd.CargasVaciadas.Count == 1, "el asiento CARGO_LOST");
        Assert.Equal(1L, m.Bd.CargasVaciadas[0].AccountId);
        Assert.Equal((long)p.Ultimo<BoxSpawn>().BoxId, m.Bd.CargasVaciadas[0].BoxRef);
    }

    [Fact]
    public void Sin_carga_no_se_deja_caja()
    {
        var (m, p, _) = Acorralado();

        m.Segundos(5);

        Assert.False(p.Recibio<BoxSpawn>());
        Assert.Empty(m.Bd.CargasVaciadas);
    }

    [Fact]
    public void Mientras_esta_destruido_no_vuela()
    {
        var (m, p, _) = Acorralado();
        m.Segundos(5);
        p.Limpiar();

        m.W.Post(new MoveIntentCmd(p, new MoveIntent { Seq = 99, TargetX = 5_000, TargetY = 5_000 }));
        m.Tick();

        Assert.Empty(p.Todos<EntityMove>().Where(e => e.EntityId == 1));
    }

    [Fact]
    public void Reaparecer_devuelve_la_nave_entera_a_la_base()
    {
        var (m, p, _) = Acorralado();
        m.Segundos(5);
        p.Limpiar();

        m.W.Post(new RespawnSelectCmd(p, 1));
        m.Tick();

        var nave = p.Todos<EntitySpawn>().Last(e => e.EntityId == 1);
        Assert.Equal(100ul, nave.X);          // la estacion del mapa
        Assert.Equal(100ul, nave.Y);
        Assert.Equal(1f, nave.HpPct);
        Assert.Equal(150u, p.Ultimo<HeroStats>().Hp);
    }
}

public class ResistenciaTests
{
    [Fact]
    public void Un_fallo_persistiendo_no_tumba_el_loop()
    {
        // la leccion del TickManager legado: una excepcion jamas mata el bucle
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500).Construir();
        var p = m.Entrar(1);
        m.Bd.RevientaAlGuardar = true;

        m.Segundos(35);               // dispara el write-behind (cada 30 s)
        m.Bd.RevientaAlGuardar = false;
        p.Limpiar();

        // el mundo sigue atendiendo comandos despues del desastre
        m.W.Post(new MoveIntentCmd(p, new MoveIntent { Seq = 1, TargetX = 11_000, TargetY = 6_000 }));
        m.Tick();

        Assert.Contains(p.Todos<EntityMove>(), e => e.EntityId == 1);
    }
}
