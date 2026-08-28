// Caracterizacion de los NPC: la maquina de tres estados portada del legado, la
// huida de los cobardes, la regeneracion de escudo, el DMZ de la estacion y lo
// que pasa cuando uno cae.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class NpcTests
{
    private static Mundo SinEstacion() => new Mundo().ConMapa(20_800, 12_800, 1, 1, 0);

    private static NpcSpawnInfo Bicho(string code = "vex", uint hp = 1_000, uint escudo = 0,
        ushort velocidad = 0, uint danio = 50, bool agresivo = false, byte huyeAl = 0,
        uint aggro = 0, uint respawn = 30, uint recompensa = 0,
        uint dropMin = 0, uint dropMax = 0) =>
        new(1, code, code, hp, escudo, velocidad, danio, agresivo, huyeAl, aggro,
            respawn, 1, 0, 0, recompensa, dropMin, dropMax);

    // ─── vagabundeo ─────────────────────────────────────────────────────────

    [Fact]
    public void Sin_presa_y_quieto_el_NPC_cruza_el_mapa()
    {
        // mapa donde el bicho cae siempre dentro del rango de relevancia: lo que
        // se caracteriza aqui es COMO elige rumbo, no si se ve
        var m = new Mundo().ConMapa(3_000, 3_000, 1, 1, 0)
            .ConNpc(Bicho(velocidad: 200)).Construir();
        var p = m.Entrar(1, datos: m.Piloto(1, x: 1_500, y: 1_500));
        var npc = m.PrimerNpc();

        // NO se limpia el buffer: el bicho elige rumbo en su PRIMER pensamiento,
        // que cae en el mismo tick del Join, y despues no vuelve a elegir hasta
        // llegar. Limpiar aqui era tirar justo el unico frame que importa.
        m.Segundos(3);                // piensa una vez por segundo

        var rumbo = p.Todos<EntityMove>().Where(e => e.EntityId == npc.Id).ToList();
        Assert.NotEmpty(rumbo);
        // el destino sale de los LIMITES DEL MAPA, no de una constante en codigo
        Assert.All(rumbo, r =>
        {
            Assert.InRange(r.TargetX, 500ul, m.Mapa.BoundsX - 500);
            Assert.InRange(r.TargetY, 500ul, m.Mapa.BoundsY - 500);
        });
    }

    [Fact]
    public void Con_presa_se_coloca_en_el_circulo_de_300_y_no_encima()
    {
        // mapa pequeño y jugador QUIETO: el bicho nace ya dentro del aggro, asi
        // que no hace falta volar hasta el —y volar movia la referencia contra la
        // que se mide el circulo.
        var m = new Mundo().ConMapa(2_000, 2_000, 1, 1, 0)
            .ConNpc(Bicho(agresivo: true, aggro: 2_000)).Construir();
        var p = m.Entrar(1, datos: m.Piloto(1, x: 1_000, y: 1_000));
        var npc = m.PrimerNpc();

        m.Segundos(3);

        var aproximaciones = p.Todos<EntityMove>().Where(e => e.EntityId == npc.Id).ToList();
        Assert.NotEmpty(aproximaciones);
        Assert.All(aproximaciones, a =>
        {
            var dist = Math.Sqrt(Math.Pow((double)a.TargetX - 1_000, 2)
                                 + Math.Pow((double)a.TargetY - 1_000, 2));
            // se coloca en el CIRCULO de 300, no encima del jugador
            Assert.InRange(dist, 298, 302);
        });
    }

    // ─── pasivo no es inofensivo ────────────────────────────────────────────

    [Fact]
    public void Un_pasivo_golpeado_devuelve_el_fuego()
    {
        var m = SinEstacion().ConNpc(Bicho(hp: 100_000, agresivo: false, aggro: 0)).Construir();
        var p = m.Entrar(1, danioLaser: 10, escudo: 0, datos: null);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        p.Limpiar();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Segundos(3);

        // el ReceiveAttack del legado: quien le pega se vuelve su objetivo
        Assert.Contains(p.Todos<AttackEvent>(), a => a.AttackerId == npc.Id);
    }

    [Fact]
    public void Con_el_combate_NPC_apagado_persiguen_pero_no_pegan()
    {
        var m = new Mundo().ConMapa(2_000, 2_000, 1, 1, 0).SinCombateNpc()
            .ConNpc(Bicho(agresivo: true, aggro: 2_000, velocidad: 200)).Construir();
        var p = m.Entrar(1, escudo: 0, datos: m.Piloto(1, x: 1_000, y: 1_000));
        var npc = m.PrimerNpc();

        m.Segundos(5);

        Assert.Contains(p.Todos<EntityMove>(), e => e.EntityId == npc.Id);
        Assert.DoesNotContain(p.Todos<AttackEvent>(), a => a.AttackerId == npc.Id);
    }

    // ─── el DMZ de la estacion ──────────────────────────────────────────────

    [Fact]
    public void Dentro_de_la_zona_segura_el_NPC_no_elige_presa()
    {
        var m = new Mundo().ConMapa(2_000, 2_000, 1_000, 1_000, 1_500)
            .ConNpc(Bicho(agresivo: true, aggro: 2_000, danio: 100)).Construir();
        var p = m.Entrar(1, escudo: 0, datos: m.Piloto(1, hp: 100_000, x: 1_000, y: 1_000));
        // OJO: se descarta el PRIMER tick a proposito. `EnBase` lo calcula
        // `ActualizarRangoBase`, que corre DESPUES de `PensarNpc`, asi que en el
        // tick del Join el bicho todavia ve al jugador como si estuviera fuera y
        // puede colar un disparo. Es un agujero real de un tick, anotado aparte;
        // esta prueba fija el DMZ, que es lo que rige del segundo tick en adelante.
        m.Tick();
        p.Limpiar();

        m.Segundos(6);

        Assert.DoesNotContain(p.Todos<AttackEvent>(), a => a.AttackerId >= 1_000_000);
    }

    [Fact]
    public void Fuera_de_la_zona_segura_el_mismo_NPC_si_dispara()
    {
        // el control del anterior: mismo montaje, sin estacion
        var m = new Mundo().ConMapa(2_000, 2_000, 1, 1, 0)
            .ConNpc(Bicho(agresivo: true, aggro: 700, danio: 100)).Construir();
        var p = m.Entrar(1, escudo: 0, datos: m.Piloto(1, hp: 100_000, x: 1_000, y: 1_000));

        m.Segundos(6);

        Assert.Contains(p.Todos<AttackEvent>(), a => a.AttackerId >= 1_000_000);
    }

    // ─── los cobardes ───────────────────────────────────────────────────────

    [Fact]
    public void Bajo_su_umbral_el_cobarde_corre_en_direccion_contraria()
    {
        var m = SinEstacion().ConNpc(Bicho(hp: 1_000, huyeAl: 30, agresivo: true, aggro: 700))
            .Construir();
        var p = m.Entrar(1, danioLaser: 400, escudo: 0, datos: m.Piloto(1, hp: 100_000));
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        var (jugadorX, jugadorY) = m.Nave(1);
        var distanciaPrevia = Math.Sqrt(Math.Pow(npc.X - jugadorX, 2) + Math.Pow(npc.Y - jugadorY, 2));
        p.Limpiar();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Segundos(2);                // 400 x 3 golpes deja el casco por debajo del 30%

        Assert.True(npc.Hp * 100 / npc.MaxHp < 30, "la prueba necesita el casco bajo el umbral");
        // no vale mirar el ULTIMO movimiento: el frenazo del golpe se emite
        // despues de la huida dentro del mismo tick. Lo que se afirma es que
        // EXISTE un rumbo que lo aleja, y por mucho (HuidaDistancia son 2500).
        var rumbos = p.Todos<EntityMove>().Where(e => e.EntityId == npc.Id)
            .Select(e => Math.Sqrt(Math.Pow((double)e.TargetX - jugadorX, 2)
                                   + Math.Pow((double)e.TargetY - jugadorY, 2)))
            .ToList();
        Assert.Contains(rumbos, d => d > distanciaPrevia + 1_000);
    }

    [Fact]
    public void El_cobarde_en_huida_deja_de_disparar()
    {
        var m = SinEstacion().ConNpc(Bicho(hp: 1_000, huyeAl: 30, agresivo: true, aggro: 700))
            .Construir();
        var p = m.Entrar(1, danioLaser: 400, escudo: 0, datos: m.Piloto(1, hp: 100_000));
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Segundos(2);
        m.W.Post(new LaserToggleCmd(p, false));
        m.Tick();
        p.Limpiar();

        m.Segundos(8);                // sigue dentro de los 12 s de huida

        Assert.DoesNotContain(p.Todos<AttackEvent>(), a => a.AttackerId == npc.Id);
    }

    // ─── escudo ─────────────────────────────────────────────────────────────

    [Fact]
    public void El_escudo_del_NPC_se_recompone_tras_diez_segundos_de_tregua()
    {
        var m = SinEstacion().SinCombateNpc()
            .ConNpc(Bicho(hp: 100_000, escudo: 1_000)).Construir();
        var p = m.Entrar(1, danioLaser: 500);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        m.W.Post(new LaserToggleCmd(p, false));
        m.Tick();
        Assert.Equal(500u, npc.Shield);

        m.Segundos(9);
        Assert.Equal(500u, npc.Shield);   // todavia dentro de los 10 s de combate

        m.Segundos(4);
        Assert.True(npc.Shield > 500, $"el escudo no regenero: {npc.Shield}");
        Assert.True(npc.Shield <= npc.MaxShield);
    }

    // ─── su muerte ──────────────────────────────────────────────────────────

    [Fact]
    public void Al_caer_deja_caja_credits_y_su_reaparicion_programada()
    {
        var m = SinEstacion().ConNpc(Bicho(hp: 100, recompensa: 250, dropMin: 30, dropMax: 30))
            .Construir();
        var p = m.Entrar(1, danioLaser: 100);
        var npc = m.PrimerNpc();
        var npcId = npc.Id;
        m.Acercar(p, npc, 100);
        p.Limpiar();

        m.W.Post(new SelectTargetCmd(p, npcId));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();

        var muerte = p.Ultimo<EntityDestroyed>();
        Assert.Equal(npcId, muerte.EntityId);
        Assert.Equal(1ul, muerte.KillerId);

        var caja = p.Ultimo<BoxSpawn>();
        Assert.Equal((ulong)Math.Round(npc.X), caja.X);
        Assert.Equal((ulong)Math.Round(npc.Y), caja.Y);

        // los credits se asientan SIEMPRE relativos y con su motivo
        Espera.A(() => m.Bd.Creditos.Count == 1, "el asiento de credits");
        Assert.Equal((1L, 250m, "NPC_KILL", (long?)npcId), m.Bd.Creditos[0]);

        // y vuelve cuando toca: respawn_seconds del catalogo
        Assert.DoesNotContain(npcId, m.W.NpcsVivos.Keys);
        m.Segundos(31);
        Assert.Contains(npcId, m.W.NpcsVivos.Keys);
    }

    [Fact]
    public void Su_muerte_suelta_el_objetivo_de_quien_lo_mato()
    {
        var m = SinEstacion().ConNpc(Bicho(hp: 100)).Construir();
        var p = m.Entrar(1, danioLaser: 100);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        p.Limpiar();

        m.Segundos(3);
        // el laser se apago solo: sin objetivo no hay mas golpes
        Assert.Empty(p.Todos<AttackEvent>());
    }
}
