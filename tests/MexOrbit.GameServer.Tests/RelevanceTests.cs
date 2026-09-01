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

public class RelevanceTests
{
    /// <summary>Mapa grande y sin estacion, para tener sitio donde alejarse.</summary>
    private static TestWorld Sector() => new TestWorld().WithMap(20_800, 12_800, 1, 1, 0);

    /// <summary>Un maniqui quieto y ciego, y el jugador a `distancia` de el.</summary>
    private static (TestWorld M, FakePort P, Entity Npc) A(double distance, uint hold = 300)
    {
        var m = Sector().WithDummy(hp: 100_000).Build();
        var npc = m.FirstNpc();
        var p = m.Enter(1, data: m.Pilot(1, hold: hold,
            x: (int)Math.Round(npc.X + distance), y: (int)Math.Round(npc.Y)));
        return (m, p, npc);
    }

    // ─── entrar al mapa ─────────────────────────────────────────────────────

    [Fact]
    public void On_entering_only_what_is_in_range_arrives()
    {
        var (_, cerca, npc) = A(1_500);
        Assert.Contains(cerca.All<EntitySpawn>(), e => e.EntityId == npc.Id);

        var (_, lejos, other) = A(5_000);
        Assert.DoesNotContain(lejos.All<EntitySpawn>(), e => e.EntityId == other.Id);
    }

    [Fact]
    public void A_distant_npc_does_not_spend_a_single_frame_moving()
    {
        // el ahorro de verdad no son los spawns, son los movimientos: 54 bichos
        // eligiendo rumbo una vez por segundo, a cada jugador del mapa
        var m = Sector().WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 1_000, 0, 200, 50, false, 0, 0,
            30, 1, 0, 0, 0, 0, 0)).Build();
        var npc = m.FirstNpc();
        var p = m.Enter(1, data: m.Pilot(1,
            x: (int)Math.Clamp(npc.X + 6_000, 0, 20_800), y: (int)Math.Round(npc.Y)));
        p.Clear();

        m.Seconds(5);

        Assert.DoesNotContain(p.All<EntityMove>(), e => e.EntityId == npc.Id);
    }

    // ─── entrar y salir de rango volando ────────────────────────────────────

    [Fact]
    public void Getting_closer_brings_the_beast_and_moving_away_removes_it()
    {
        var (m, p, npc) = A(5_000);
        Assert.DoesNotContain(p.All<EntitySpawn>(), e => e.EntityId == npc.Id);

        m.MoveTo(p, npc.X + 1_000, npc.Y);
        Assert.Contains(p.All<EntitySpawn>(), e => e.EntityId == npc.Id);
        p.Clear();

        m.MoveTo(p, npc.X + 5_000, npc.Y);
        var salida = Assert.Single(p.All<EntityDespawn>().Where(e => e.EntityId == npc.Id));
        // la razon VIAJA, no se infiere: para el cliente no es lo mismo que se
        // haya ido de la pantalla a que lo hayan reventado
        Assert.Equal(DespawnReason.Range, salida.Reason);
    }

    [Fact]
    public void What_enters_range_with_a_heading_brings_its_heading_too()
    {
        // `EntitySpawn` no lleva destino. Sin esto, una nave que entra en rango en
        // pleno vuelo aparece CONGELADA hasta su siguiente movimiento — que puede
        // tardar segundos, o no llegar nunca si ya iba camino de su destino.
        var (m, p, npc) = A(5_000);
        m.Seconds(2);                     // le da tiempo a elegir rumbo
        Assert.True(npc.Moving, "la prueba necesita un bicho con destino activo");
        p.Clear();

        m.MoveTo(p, npc.X + 1_000, npc.Y);

        Assert.Contains(p.All<EntitySpawn>(), e => e.EntityId == npc.Id);
        Assert.Contains(p.All<EntityMove>(), e => e.EntityId == npc.Id);
    }

    [Fact]
    public void Hysteresis_prevents_flicker_at_the_edge()
    {
        // Entra a 2000 y no sale hasta 2200. En el legado el umbral era uno solo:
        // un jugador parado en el borde generaba un spawn y un despawn CADA tick.
        var (m, p, npc) = A(1_900);
        Assert.Contains(p.All<EntitySpawn>(), e => e.EntityId == npc.Id);
        p.Clear();

        m.MoveTo(p, npc.X + 2_100, npc.Y);   // pasado el umbral de entrada...
        Assert.Empty(p.All<EntityDespawn>());   // ...pero dentro de la banda

        m.MoveTo(p, npc.X + 2_300, npc.Y);   // fuera de la banda: ahora si
        Assert.Contains(p.All<EntityDespawn>(), e => e.EntityId == npc.Id);
    }

    // ─── el objetivo seleccionado ───────────────────────────────────────────

    [Fact]
    public void The_selected_target_never_leaves_relevance()
    {
        // spec del protocolo §relevancia. Si no, perseguir a un bicho que huye
        // seria verlo evaporarse justo cuando importa, y el server seguiria
        // diciendo que lo tienes fichado.
        var (m, p, npc) = A(500);
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();
        p.Clear();

        m.MoveTo(p, npc.X + 8_000, npc.Y);

        Assert.Empty(p.All<EntityDespawn>());
    }

    [Fact]
    public void Dropping_the_distant_target_does_remove_it()
    {
        var (m, p, npc) = A(500);
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();
        m.MoveTo(p, npc.X + 8_000, npc.Y);
        p.Clear();

        m.W.Post(new SelectTargetCmd(p, 0));      // deseleccionar
        m.Tick();

        Assert.Contains(p.All<EntityDespawn>(), e => e.EntityId == npc.Id);
    }

    [Fact]
    public void You_cannot_target_what_you_cannot_see()
    {
        // el cliente solo puede pinchar lo que recibio; esto cierra el atajo de
        // mandar un id cualquiera para que el server te informe de —y te
        // mantenga en relevancia— un bicho al otro lado del mapa
        var (m, p, npc) = A(5_000);
        p.Clear();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();

        Assert.False(p.Received<TargetInfo>());
        Assert.Empty(p.All<EntitySpawn>().Where(e => e.EntityId == npc.Id));
    }

    // ─── jugadores ──────────────────────────────────────────────────────────

    [Fact]
    public void Two_distant_players_do_not_see_each_other_until_they_close_in()
    {
        var m = Sector().Build();
        var quieto = m.Enter(1, data: m.Pilot(1, "Ana", x: 5_000, y: 6_000));
        var viajero = m.Enter(2, data: m.Pilot(2, "Beto", x: 15_000, y: 6_000));
        m.Tick();
        Assert.DoesNotContain(quieto.All<EntitySpawn>(), e => e.EntityId == 2);
        quieto.Clear();

        m.MoveTo(viajero, 6_000, 6_000);

        Assert.Contains(quieto.All<EntitySpawn>(), e => e.EntityId == 2);
    }

    [Fact]
    public void The_hero_gets_its_own_echo_wherever_it_is()
    {
        // contra esto reconcilia el cliente: si su propio movimiento dependiera
        // de la relevancia, volar lejos de todos romperia el vuelo
        var m = Sector().Build();
        var p = m.Enter(1, data: m.Pilot(1, x: 500, y: 500));
        p.Clear();

        m.W.Post(new MoveIntentCmd(p, 1, 19_000, 12_000));
        m.Tick();

        Assert.Contains(p.All<EntityMove>(), e => e.EntityId == 1);
    }

    [Fact]
    public void A_shot_is_seen_by_whoever_sees_either_side()
    {
        var (m, tirador, npc) = A(300);
        var testigo = m.Enter(2, data: m.Pilot(2, "Testigo",
            x: (int)Math.Round(npc.X + 300), y: (int)Math.Round(npc.Y + 100)));
        m.W.Post(new SelectTargetCmd(tirador, npc.Id));
        m.W.Post(new LaserToggleCmd(tirador, true));
        testigo.Clear();

        m.Tick();

        Assert.Contains(testigo.All<AttackEvent>(), a => a.AttackerId == 1 && a.TargetId == npc.Id);
    }

    // ─── cajas ──────────────────────────────────────────────────────────────

    [Fact]
    public void Boxes_enter_at_their_own_shorter_range()
    {
        // 1250, no 2000: una caja es mobiliario menudo y no hace falta verla
        // desde tan lejos como una nave
        var m = Sector().WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
            30, 1, 0, 0, 0, 30, 30)).Build();
        var npc = m.FirstNpc();
        var p = m.Enter(1, laserDamage: 100, data: m.Pilot(1,
            x: (int)Math.Round(npc.X + 200), y: (int)Math.Round(npc.Y)));
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        var box = p.Last<BoxSpawn>();
        p.Clear();

        // a 1600 la nave ya no se veria... pero la caja tampoco: su rango es menor
        m.MoveTo(p, npc.X + 1_600, npc.Y);
        Assert.Contains(p.All<BoxDespawn>(), b => b.BoxId == box.BoxId
            && b.Reason == BoxDespawnReason.Range);
        p.Clear();

        m.MoveTo(p, npc.X + 200, npc.Y);
        Assert.Contains(p.All<BoxSpawn>(), b => b.BoxId == box.BoxId);
    }

    [Fact]
    public void An_npc_does_not_respawn_on_top_of_anyone()
    {
        // Reaparecia en un punto sorteado sin mirar a nadie: podia materializarse
        // a 500 unidades, en mitad de la pantalla y de la nada.
        var m = Sector().WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
            30, 1, 0, 0, 0, 0, 0)).Build();
        var npc = m.FirstNpc();
        var npcId = npc.Id;
        var p = m.Enter(1, laserDamage: 100, data: m.Pilot(1,
            x: (int)Math.Round(npc.X + 200), y: (int)Math.Round(npc.Y)));
        m.W.Post(new SelectTargetCmd(p, npcId));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        p.Clear();

        m.Seconds(31);

        // nace fuera de relevancia: entra en escena volando desde fuera, no
        // apareciendo en las narices del jugador
        var ship = m.W.ShipOf(1)!;
        var vuelto = m.W.LiveNpcs[npcId];
        var dist = Geometry.Distance(ship, vuelto);
        Assert.True(dist > 2_000, $"reaparecio a {dist:F0} u del jugador");
        Assert.Empty(p.All<EntitySpawn>().Where(e => e.EntityId == npcId));
    }

    // ─── ids que se reutilizan ──────────────────────────────────────────────

    [Fact]
    public void An_npc_that_dies_and_respawns_with_its_id_is_announced_again()
    {
        // los NPC REUTILIZAN su id al reaparecer. Si al morir no se olvidara,
        // el bicho volveria al mapa sin que el cliente se enterase nunca.
        //
        // Mapa diminuto a proposito: reaparece en un punto SORTEADO, no donde
        // cayo, y aqui cualquier punto del mapa esta en rango. Con el sector
        // entero, la prueba dependeria de donde cayo el dado.
        var m = new TestWorld().WithMap(2_000, 2_000, 1, 1, 0)
            .WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 100, 0, 0, 50, false, 0, 0,
            30, 1, 0, 0, 0, 0, 0)).Build();
        var npc = m.FirstNpc();
        var npcId = npc.Id;
        var p = m.Enter(1, laserDamage: 100, data: m.Pilot(1,
            x: (int)Math.Round(npc.X + 200), y: (int)Math.Round(npc.Y)));
        m.W.Post(new SelectTargetCmd(p, npcId));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        Assert.Contains(p.All<EntityDestroyed>(), e => e.EntityId == npcId);
        p.Clear();

        m.Seconds(31);

        Assert.Contains(p.All<EntitySpawn>(), e => e.EntityId == npcId);
    }
}
