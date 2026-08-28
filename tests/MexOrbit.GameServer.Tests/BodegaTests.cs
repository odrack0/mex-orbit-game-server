// Caracterizacion de la bodega volante y la base: recoger cajas, la frontera del
// rango de la estacion, descargar y vender.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class BodegaTests
{
    private const long Asterium = 10;

    /// <summary>Un mundo con una caja en el suelo: se consigue matando al bicho,
    /// que es como se consiguen de verdad.</summary>
    private static (Mundo M, PuertoFalso P, ulong CajaId) ConCajaEnElSuelo(
        uint bodega = 300, Dictionary<long, uint>? carga = null)
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 1, 1, 0)
            .ConNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
                30, 1, 0, 0, 0, 30, 30))
            .Construir();
        var p = m.Entrar(1, danioLaser: 100, carga: carga, datos: m.Piloto(1, bodega: bodega));
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        var caja = p.Ultimo<BoxSpawn>();
        p.Limpiar();
        return (m, p, caja.BoxId);
    }

    // ─── recoleccion ────────────────────────────────────────────────────────

    [Fact]
    public void Recoger_dentro_de_rango_llena_la_bodega_y_asienta_la_recogida()
    {
        var (m, p, cajaId) = ConCajaEnElSuelo();

        m.W.Post(new CollectBoxCmd(p, 7, cajaId));
        m.Tick();

        var resultado = p.Ultimo<CollectResult>();
        Assert.Equal(7ul, resultado.RequestId);
        Assert.Single(resultado.Drops);
        Assert.Equal("asterium", resultado.Drops[0].MaterialId);
        Assert.Equal(30u, resultado.Drops[0].Amount);
        Assert.Equal(30u, p.Ultimo<HeroStats>().Cargo);

        // la caja queda vacia y desaparece
        Assert.Equal(BoxDespawnReason.Collected, p.Ultimo<BoxDespawn>().Reason);
        Assert.DoesNotContain(cajaId, m.W.CajasVivas);
        Assert.Single(m.Bd.Recogidas);
        Assert.Equal((long)cajaId, m.Bd.Recogidas[0].BoxRef);
    }

    [Fact]
    public void Recoger_lejos_responde_TOO_FAR()
    {
        var (m, p, cajaId) = ConCajaEnElSuelo();
        var (x, y) = m.Nave(1);
        m.MoverA(p, x + 400, y);      // CollectRange son 250
        p.Limpiar();

        m.W.Post(new CollectBoxCmd(p, 7, cajaId));
        m.Tick();

        var error = p.Ultimo<ErrorReply>();
        Assert.Equal(ErrorCode.TooFar, error.Code);
        Assert.Equal(7ul, error.RequestId);
        Assert.Contains(cajaId, m.W.CajasVivas);
    }

    [Fact]
    public void Recoger_una_caja_que_ya_no_existe_responde_GONE()
    {
        var (m, p, _) = ConCajaEnElSuelo();

        m.W.Post(new CollectBoxCmd(p, 7, 999_999));
        m.Tick();

        Assert.Equal(ErrorCode.Gone, p.Ultimo<ErrorReply>().Code);
    }

    [Fact]
    public void Con_la_bodega_llena_responde_INSUFFICIENT()
    {
        // llega a la caja con la bodega ya al tope: el hueco es 0
        var (m, p, cajaId) = ConCajaEnElSuelo(
            bodega: 300, carga: new Dictionary<long, uint> { [Asterium] = 300 });

        m.W.Post(new CollectBoxCmd(p, 7, cajaId));
        m.Tick();

        Assert.Equal(ErrorCode.Insufficient, p.Ultimo<ErrorReply>().Code);
        Assert.Contains(cajaId, m.W.CajasVivas);
    }

    [Fact]
    public void La_recogida_parcial_deja_el_resto_en_la_caja()
    {
        var (m, p, cajaId) = ConCajaEnElSuelo(bodega: 10);

        m.W.Post(new CollectBoxCmd(p, 7, cajaId));
        m.Tick();

        Assert.Equal(10u, p.Ultimo<CollectResult>().Drops[0].Amount);
        // la caja sobrevive con las 20 unidades que no cupieron
        Assert.Contains(cajaId, m.W.CajasVivas);
        Assert.Null(p.UltimoOrNull<BoxDespawn>());
    }

    [Fact]
    public void Si_la_BD_falla_la_recogida_no_miente_al_cliente()
    {
        var (m, p, cajaId) = ConCajaEnElSuelo();
        m.Bd.RevientaAlEscribir = true;

        m.W.Post(new CollectBoxCmd(p, 7, cajaId));
        m.Tick();

        Assert.Equal(ErrorCode.Generic, p.Ultimo<ErrorReply>().Code);
        Assert.Null(p.UltimoOrNull<CollectResult>());
        Assert.Empty(m.Bd.Recogidas);
    }

    [Fact]
    public void La_caja_expira_a_los_150_segundos()
    {
        var (m, p, cajaId) = ConCajaEnElSuelo();

        m.Segundos(149);
        Assert.Contains(cajaId, m.W.CajasVivas);

        m.Segundos(3);
        Assert.DoesNotContain(cajaId, m.W.CajasVivas);
        Assert.Equal(BoxDespawnReason.Expired, p.Ultimo<BoxDespawn>().Reason);
    }

    // ─── la base ────────────────────────────────────────────────────────────

    [Fact]
    public void Entrar_y_salir_del_rango_de_la_estacion_avisa_al_cliente()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500).Construir();
        var p = m.Entrar(1);   // se entra justo encima de la estacion

        Assert.True(p.Ultimo<StationRange>().InRange);
        Assert.Equal(1ul, p.Ultimo<StationRange>().StationId);

        m.MoverA(p, 13_000, 6_000);   // fuera del radio de 1500
        Assert.False(p.Ultimo<StationRange>().InRange);

        m.MoverA(p, 10_500, 6_000);   // de vuelta dentro
        Assert.True(p.Ultimo<StationRange>().InRange);
    }

    [Fact]
    public void Descargar_fuera_de_la_base_responde_TOO_FAR()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500).Construir();
        var p = m.Entrar(1);
        m.MoverA(p, 15_000, 6_000);
        p.Limpiar();

        m.W.Post(new UnloadCargoCmd(p, 3));
        m.Tick();

        var error = p.Ultimo<ErrorReply>();
        Assert.Equal(ErrorCode.TooFar, error.Code);
        Assert.Equal(3ul, error.RequestId);
    }

    [Fact]
    public void Descargar_en_la_base_devuelve_lo_almacenado_y_lo_refinado()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500)
            .ConBias(new MaterialBias(Asterium, "asterium", 1m))
            .ConReceta(new RefineRecipe(20, "aurorium", 1, new Dictionary<long, uint> { [Asterium] = 30 }))
            .Construir();
        var p = m.Entrar(1, carga: new Dictionary<long, uint> { [Asterium] = 30 });
        m.Bd.ProximaDescarga = new UnloadOutcome(
            new Dictionary<long, uint> { [Asterium] = 30 },
            new Dictionary<long, uint> { [20] = 1 });
        p.Limpiar();

        m.W.Post(new UnloadCargoCmd(p, 3));
        m.Tick();

        var resultado = p.Ultimo<UnloadResult>();
        Assert.Equal(3ul, resultado.RequestId);
        Assert.Equal("asterium", resultado.Stored[0].MaterialId);
        Assert.Equal(30u, resultado.Stored[0].Amount);
        Assert.Equal("aurorium", resultado.Refined[0].MaterialId);
        // la bodega volante queda vacia
        Assert.Equal(0u, p.Ultimo<HeroStats>().Cargo);
    }

    [Fact]
    public void Si_la_descarga_falla_en_BD_el_cliente_recibe_el_error_y_conserva_la_carga()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500).Construir();
        var p = m.Entrar(1, carga: new Dictionary<long, uint> { [Asterium] = 30 });
        m.Bd.RevientaAlEscribir = true;
        p.Limpiar();

        m.W.Post(new UnloadCargoCmd(p, 3));
        m.Tick();

        Assert.Equal(ErrorCode.Generic, p.Ultimo<ErrorReply>().Code);
        Assert.Null(p.UltimoOrNull<UnloadResult>());
    }

    // ─── venta al NPC ───────────────────────────────────────────────────────

    [Fact]
    public void Vender_en_la_base_paga_y_actualiza_el_saldo()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500)
            .ConPrecios(new NpcPrice(Asterium, "asterium", 5m)).Construir();
        var p = m.Entrar(1);
        m.Bd.ProximaVenta = (10, 50m, 1_050m);
        p.Limpiar();

        m.W.Post(new SellToNpcCmd(p, 4, "asterium", 10));
        m.Tick();

        var venta = p.Ultimo<SellResult>();
        Assert.Equal(4ul, venta.RequestId);
        Assert.Equal(50ul, venta.CreditsGained);
        Assert.Equal(1_050ul, venta.NewCredits);
        Assert.Equal(1_050ul, p.Ultimo<HeroStats>().Credits);
    }

    [Fact]
    public void El_NPC_no_compra_lo_que_no_esta_en_su_tabla()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500)
            .ConPrecios(new NpcPrice(Asterium, "asterium", 5m)).Construir();
        var p = m.Entrar(1);
        p.Limpiar();

        m.W.Post(new SellToNpcCmd(p, 4, "prometium", 10));
        m.Tick();

        Assert.Equal(ErrorCode.Invalid, p.Ultimo<ErrorReply>().Code);
    }

    [Fact]
    public void Vender_sin_existencias_responde_INSUFFICIENT()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500)
            .ConPrecios(new NpcPrice(Asterium, "asterium", 5m)).Construir();
        var p = m.Entrar(1);
        m.Bd.ProximaVenta = (0, 0m, 0m);
        p.Limpiar();

        m.W.Post(new SellToNpcCmd(p, 4, "asterium", 10));
        m.Tick();

        Assert.Equal(ErrorCode.Insufficient, p.Ultimo<ErrorReply>().Code);
    }

    [Fact]
    public void Vender_fuera_de_la_base_responde_TOO_FAR()
    {
        var m = new Mundo().ConMapa(20_800, 12_800, 10_000, 6_000, 1_500)
            .ConPrecios(new NpcPrice(Asterium, "asterium", 5m)).Construir();
        var p = m.Entrar(1);
        m.MoverA(p, 15_000, 6_000);
        p.Limpiar();

        m.W.Post(new SellToNpcCmd(p, 4, "asterium", 10));
        m.Tick();

        Assert.Equal(ErrorCode.TooFar, p.Ultimo<ErrorReply>().Code);
    }
}
