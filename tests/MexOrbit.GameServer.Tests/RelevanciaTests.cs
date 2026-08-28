// Relevancia por rango: el cliente solo sabe de lo que tiene cerca.
//
// El truco de estas pruebas es que el jugador se coloca RESPECTO AL BICHO. Los
// NPC nacen en un punto sorteado del mapa, asi que fijar al jugador en una
// coordenada absoluta haria la distancia —lo unico que se esta midiendo— cosa
// del azar. Se lee donde cayo el bicho y se entra a la distancia que toca.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class RelevanciaTests
{
    /// <summary>Mapa grande y sin estacion, para tener sitio donde alejarse.</summary>
    private static Mundo Sector() => new Mundo().ConMapa(20_800, 12_800, 1, 1, 0);

    /// <summary>Un maniqui quieto y ciego, y el jugador a `distancia` de el.</summary>
    private static (Mundo M, PuertoFalso P, Entity Npc) A(double distancia, uint bodega = 300)
    {
        var m = Sector().ConManiqui(hp: 100_000).Construir();
        var npc = m.PrimerNpc();
        var p = m.Entrar(1, datos: m.Piloto(1, bodega: bodega,
            x: (uint)Math.Round(npc.X + distancia), y: (uint)Math.Round(npc.Y)));
        return (m, p, npc);
    }

    // ─── entrar al mapa ─────────────────────────────────────────────────────

    [Fact]
    public void Al_entrar_solo_llega_lo_que_esta_en_rango()
    {
        var (_, cerca, npc) = A(1_500);
        Assert.Contains(cerca.Todos<EntitySpawn>(), e => e.EntityId == npc.Id);

        var (_, lejos, otro) = A(5_000);
        Assert.DoesNotContain(lejos.Todos<EntitySpawn>(), e => e.EntityId == otro.Id);
    }

    [Fact]
    public void Un_NPC_lejano_no_gasta_ni_un_frame_en_moverse()
    {
        // el ahorro de verdad no son los spawns, son los movimientos: 54 bichos
        // eligiendo rumbo una vez por segundo, a cada jugador del mapa
        var m = Sector().ConNpc(new NpcSpawnInfo(1, "vex", "Vex", 1_000, 0, 200, 50, false, 0, 0,
            30, 1, 0, 0, 0, 0, 0)).Construir();
        var npc = m.PrimerNpc();
        var p = m.Entrar(1, datos: m.Piloto(1,
            x: (uint)Math.Clamp(npc.X + 6_000, 0, 20_800), y: (uint)Math.Round(npc.Y)));
        p.Limpiar();

        m.Segundos(5);

        Assert.DoesNotContain(p.Todos<EntityMove>(), e => e.EntityId == npc.Id);
    }

    // ─── entrar y salir de rango volando ────────────────────────────────────

    [Fact]
    public void Acercarse_trae_al_bicho_y_alejarse_lo_retira()
    {
        var (m, p, npc) = A(5_000);
        Assert.DoesNotContain(p.Todos<EntitySpawn>(), e => e.EntityId == npc.Id);

        m.MoverA(p, npc.X + 1_000, npc.Y);
        Assert.Contains(p.Todos<EntitySpawn>(), e => e.EntityId == npc.Id);
        p.Limpiar();

        m.MoverA(p, npc.X + 5_000, npc.Y);
        var salida = Assert.Single(p.Todos<EntityDespawn>().Where(e => e.EntityId == npc.Id));
        // la razon VIAJA, no se infiere: para el cliente no es lo mismo que se
        // haya ido de la pantalla a que lo hayan reventado
        Assert.Equal(DespawnReason.Range, salida.Reason);
    }

    [Fact]
    public void Lo_que_entra_en_rango_con_rumbo_trae_tambien_su_rumbo()
    {
        // `EntitySpawn` no lleva destino. Sin esto, una nave que entra en rango en
        // pleno vuelo aparece CONGELADA hasta su siguiente movimiento — que puede
        // tardar segundos, o no llegar nunca si ya iba camino de su destino.
        var (m, p, npc) = A(5_000);
        m.Segundos(2);                     // le da tiempo a elegir rumbo
        Assert.True(npc.Moving, "la prueba necesita un bicho con destino activo");
        p.Limpiar();

        m.MoverA(p, npc.X + 1_000, npc.Y);

        Assert.Contains(p.Todos<EntitySpawn>(), e => e.EntityId == npc.Id);
        Assert.Contains(p.Todos<EntityMove>(), e => e.EntityId == npc.Id);
    }

    [Fact]
    public void La_histeresis_impide_el_parpadeo_en_el_borde()
    {
        // Entra a 2000 y no sale hasta 2200. En el legado el umbral era uno solo:
        // un jugador parado en el borde generaba un spawn y un despawn CADA tick.
        var (m, p, npc) = A(1_900);
        Assert.Contains(p.Todos<EntitySpawn>(), e => e.EntityId == npc.Id);
        p.Limpiar();

        m.MoverA(p, npc.X + 2_100, npc.Y);   // pasado el umbral de entrada...
        Assert.Empty(p.Todos<EntityDespawn>());   // ...pero dentro de la banda

        m.MoverA(p, npc.X + 2_300, npc.Y);   // fuera de la banda: ahora si
        Assert.Contains(p.Todos<EntityDespawn>(), e => e.EntityId == npc.Id);
    }

    // ─── el objetivo seleccionado ───────────────────────────────────────────

    [Fact]
    public void El_objetivo_seleccionado_jamas_sale_de_relevancia()
    {
        // spec del protocolo §relevancia. Si no, perseguir a un bicho que huye
        // seria verlo evaporarse justo cuando importa, y el server seguiria
        // diciendo que lo tienes fichado.
        var (m, p, npc) = A(500);
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();
        p.Limpiar();

        m.MoverA(p, npc.X + 8_000, npc.Y);

        Assert.Empty(p.Todos<EntityDespawn>());
    }

    [Fact]
    public void Soltar_el_objetivo_lejano_si_lo_retira()
    {
        var (m, p, npc) = A(500);
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();
        m.MoverA(p, npc.X + 8_000, npc.Y);
        p.Limpiar();

        m.W.Post(new SelectTargetCmd(p, 0));      // deseleccionar
        m.Tick();

        Assert.Contains(p.Todos<EntityDespawn>(), e => e.EntityId == npc.Id);
    }

    [Fact]
    public void No_se_puede_fichar_lo_que_no_se_ve()
    {
        // el cliente solo puede pinchar lo que recibio; esto cierra el atajo de
        // mandar un id cualquiera para que el server te informe de —y te
        // mantenga en relevancia— un bicho al otro lado del mapa
        var (m, p, npc) = A(5_000);
        p.Limpiar();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();

        Assert.False(p.Recibio<TargetInfo>());
        Assert.Empty(p.Todos<EntitySpawn>().Where(e => e.EntityId == npc.Id));
    }

    // ─── jugadores ──────────────────────────────────────────────────────────

    [Fact]
    public void Dos_jugadores_lejanos_no_se_ven_y_al_acercarse_si()
    {
        var m = Sector().Construir();
        var quieto = m.Entrar(1, datos: m.Piloto(1, "Ana", x: 5_000, y: 6_000));
        var viajero = m.Entrar(2, datos: m.Piloto(2, "Beto", x: 15_000, y: 6_000));
        m.Tick();
        Assert.DoesNotContain(quieto.Todos<EntitySpawn>(), e => e.EntityId == 2);
        quieto.Limpiar();

        m.MoverA(viajero, 6_000, 6_000);

        Assert.Contains(quieto.Todos<EntitySpawn>(), e => e.EntityId == 2);
    }

    [Fact]
    public void El_heroe_recibe_su_propio_eco_este_donde_este()
    {
        // contra esto reconcilia el cliente: si su propio movimiento dependiera
        // de la relevancia, volar lejos de todos romperia el vuelo
        var m = Sector().Construir();
        var p = m.Entrar(1, datos: m.Piloto(1, x: 500, y: 500));
        p.Limpiar();

        m.W.Post(new MoveIntentCmd(p, 1, 19_000, 12_000));
        m.Tick();

        Assert.Contains(p.Todos<EntityMove>(), e => e.EntityId == 1);
    }

    [Fact]
    public void Un_disparo_lo_ve_quien_vea_a_cualquiera_de_los_dos()
    {
        var (m, tirador, npc) = A(300);
        var testigo = m.Entrar(2, datos: m.Piloto(2, "Testigo",
            x: (uint)Math.Round(npc.X + 300), y: (uint)Math.Round(npc.Y + 100)));
        m.W.Post(new SelectTargetCmd(tirador, npc.Id));
        m.W.Post(new LaserToggleCmd(tirador, true));
        testigo.Limpiar();

        m.Tick();

        Assert.Contains(testigo.Todos<AttackEvent>(), a => a.AttackerId == 1 && a.TargetId == npc.Id);
    }

    // ─── cajas ──────────────────────────────────────────────────────────────

    [Fact]
    public void Las_cajas_entran_a_su_propio_rango_mas_corto()
    {
        // 1250, no 2000: una caja es mobiliario menudo y no hace falta verla
        // desde tan lejos como una nave
        var m = Sector().ConNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
            30, 1, 0, 0, 0, 30, 30)).Construir();
        var npc = m.PrimerNpc();
        var p = m.Entrar(1, danioLaser: 100, datos: m.Piloto(1,
            x: (uint)Math.Round(npc.X + 200), y: (uint)Math.Round(npc.Y)));
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        var caja = p.Ultimo<BoxSpawn>();
        p.Limpiar();

        // a 1600 la nave ya no se veria... pero la caja tampoco: su rango es menor
        m.MoverA(p, npc.X + 1_600, npc.Y);
        Assert.Contains(p.Todos<BoxDespawn>(), b => b.BoxId == caja.BoxId
            && b.Reason == BoxDespawnReason.Range);
        p.Limpiar();

        m.MoverA(p, npc.X + 200, npc.Y);
        Assert.Contains(p.Todos<BoxSpawn>(), b => b.BoxId == caja.BoxId);
    }

    [Fact]
    public void Un_NPC_no_reaparece_encima_de_nadie()
    {
        // Reaparecia en un punto sorteado sin mirar a nadie: podia materializarse
        // a 500 unidades, en mitad de la pantalla y de la nada.
        var m = Sector().ConNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
            30, 1, 0, 0, 0, 0, 0)).Construir();
        var npc = m.PrimerNpc();
        var npcId = npc.Id;
        var p = m.Entrar(1, danioLaser: 100, datos: m.Piloto(1,
            x: (uint)Math.Round(npc.X + 200), y: (uint)Math.Round(npc.Y)));
        m.W.Post(new SelectTargetCmd(p, npcId));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        p.Limpiar();

        m.Segundos(31);

        // nace fuera de relevancia: entra en escena volando desde fuera, no
        // apareciendo en las narices del jugador
        var nave = m.W.NaveDe(1)!;
        var vuelto = m.W.NpcsVivos[npcId];
        var dist = Geometria.Distancia(nave, vuelto);
        Assert.True(dist > 2_000, $"reaparecio a {dist:F0} u del jugador");
        Assert.Empty(p.Todos<EntitySpawn>().Where(e => e.EntityId == npcId));
    }

    // ─── ids que se reutilizan ──────────────────────────────────────────────

    [Fact]
    public void Un_NPC_que_muere_y_reaparece_con_su_id_se_vuelve_a_anunciar()
    {
        // los NPC REUTILIZAN su id al reaparecer. Si al morir no se olvidara,
        // el bicho volveria al mapa sin que el cliente se enterase nunca.
        //
        // Mapa diminuto a proposito: reaparece en un punto SORTEADO, no donde
        // cayo, y aqui cualquier punto del mapa esta en rango. Con el sector
        // entero, la prueba dependeria de donde cayo el dado.
        var m = new Mundo().ConMapa(2_000, 2_000, 1, 1, 0)
            .ConNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
            30, 1, 0, 0, 0, 0, 0)).Construir();
        var npc = m.PrimerNpc();
        var npcId = npc.Id;
        var p = m.Entrar(1, danioLaser: 100, datos: m.Piloto(1,
            x: (uint)Math.Round(npc.X + 200), y: (uint)Math.Round(npc.Y)));
        m.W.Post(new SelectTargetCmd(p, npcId));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        Assert.Contains(p.Todos<EntityDestroyed>(), e => e.EntityId == npcId);
        p.Limpiar();

        m.Segundos(31);

        Assert.Contains(p.Todos<EntitySpawn>(), e => e.EntityId == npcId);
    }
}
