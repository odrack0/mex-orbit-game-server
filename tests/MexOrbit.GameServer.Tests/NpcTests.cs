// Caracterizacion de los NPC: la maquina de tres estados portada del legado, la
// huida de los cobardes, la regeneracion de escudo, el DMZ de la estacion y lo
// que pasa cuando uno cae.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class NpcTests
{
    private static TestWorld WithoutStation() => new TestWorld().WithMap(20_800, 12_800, 1, 1, 0);

    private static NpcSpawnInfo Beast(string code = "vex", uint hp = 1_000, uint shield = 0,
        ushort speed = 0, uint damage = 50, bool aggressive = false, byte fleesAt = 0,
        uint aggro = 0, uint respawn = 30, uint reward = 0,
        uint dropMin = 0, uint dropMax = 0) =>
        new(1, code, code, hp, shield, speed, damage, aggressive, fleesAt, aggro,
            respawn, 1, 0, 0, reward, dropMin, dropMax);

    // ─── vagabundeo ─────────────────────────────────────────────────────────

    [Fact]
    public void With_no_prey_and_standing_still_the_npc_crosses_the_map()
    {
        // mapa donde el bicho cae siempre dentro del rango de relevancia: lo que
        // se caracteriza aqui es COMO elige rumbo, no si se ve
        var m = new TestWorld().WithMap(3_000, 3_000, 1, 1, 0)
            .WithNpc(Beast(speed: 200)).Build();
        var p = m.Enter(1, data: m.Pilot(1, x: 1_500, y: 1_500));
        var npc = m.FirstNpc();

        // NO se limpia el buffer: el bicho elige rumbo en su PRIMER pensamiento,
        // que cae en el mismo tick del Join, y despues no vuelve a elegir hasta
        // llegar. Clear aqui era tirar justo el unico frame que importa.
        m.Seconds(3);                // piensa una vez por segundo

        var heading = p.All<EntityMove>().Where(e => e.EntityId == npc.Id).ToList();
        Assert.NotEmpty(heading);
        // el destino sale de los LIMITES DEL MAPA, no de una constante en codigo
        Assert.All(heading, r =>
        {
            Assert.InRange(r.TargetX, 500ul, m.Map.BoundsX - 500);
            Assert.InRange(r.TargetY, 500ul, m.Map.BoundsY - 500);
        });
    }

    [Fact]
    public void With_prey_it_takes_position_on_the_300_circle_not_on_top()
    {
        // mapa pequeño y jugador QUIETO: el bicho nace ya dentro del aggro, asi
        // que no hace falta volar hasta el —y volar movia la referencia contra la
        // que se mide el circulo.
        var m = new TestWorld().WithMap(2_000, 2_000, 1, 1, 0)
            .WithNpc(Beast(aggressive: true, aggro: 2_000)).Build();
        var p = m.Enter(1, data: m.Pilot(1, x: 1_000, y: 1_000));
        var npc = m.FirstNpc();

        m.Seconds(3);

        var approaches = p.All<EntityMove>().Where(e => e.EntityId == npc.Id).ToList();
        Assert.NotEmpty(approaches);
        Assert.All(approaches, a =>
        {
            var dist = Math.Sqrt(Math.Pow((double)a.TargetX - 1_000, 2)
                                 + Math.Pow((double)a.TargetY - 1_000, 2));
            // se coloca en el CIRCULO de 300, no encima del jugador
            Assert.InRange(dist, 298, 302);
        });
    }

    // ─── pasivo no es inofensivo ────────────────────────────────────────────

    [Fact]
    public void A_passive_that_is_hit_fights_back()
    {
        var m = WithoutStation().WithNpc(Beast(hp: 100_000, aggressive: false, aggro: 0)).Build();
        var p = m.Enter(1, laserDamage: 10, shield: 0, data: null);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        p.Clear();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Seconds(3);

        // el ReceiveAttack del legado: quien le pega se vuelve su objetivo
        Assert.Contains(p.All<AttackEvent>(), a => a.AttackerId == npc.Id);
    }

    [Fact]
    public void With_npc_combat_off_they_chase_but_do_not_hit()
    {
        var m = new TestWorld().WithMap(2_000, 2_000, 1, 1, 0).WithoutNpcCombat()
            .WithNpc(Beast(aggressive: true, aggro: 2_000, speed: 200)).Build();
        var p = m.Enter(1, shield: 0, data: m.Pilot(1, x: 1_000, y: 1_000));
        var npc = m.FirstNpc();

        m.Seconds(5);

        Assert.Contains(p.All<EntityMove>(), e => e.EntityId == npc.Id);
        Assert.DoesNotContain(p.All<AttackEvent>(), a => a.AttackerId == npc.Id);
    }

    // ─── el DMZ de la estacion ──────────────────────────────────────────────

    [Fact]
    public void Inside_the_safe_zone_the_npc_picks_no_prey()
    {
        var m = new TestWorld().WithMap(2_000, 2_000, 1_000, 1_000, 1_500)
            .WithNpc(Beast(aggressive: true, aggro: 2_000, damage: 100)).Build();
        var p = m.Enter(1, shield: 0, data: m.Pilot(1, hp: 100_000, x: 1_000, y: 1_000));
        // OJO: se descarta el PRIMER tick a proposito. `AtStation` lo calcula
        // `ActualizarRangoBase`, que corre DESPUES de `ThinkNpc`, asi que en el
        // tick del Join el bicho todavia ve al jugador como si estuviera fuera y
        // puede colar un disparo. Es un agujero real de un tick, anotado aparte;
        // esta prueba fija el DMZ, que es lo que rige del segundo tick en adelante.
        m.Tick();
        p.Clear();

        m.Seconds(6);

        Assert.DoesNotContain(p.All<AttackEvent>(), a => a.AttackerId >= 1_000_000);
    }

    [Fact]
    public void Inside_the_safe_zone_it_does_fight_back_if_you_shoot_first()
    {
        // La zona segura protege a quien NO ha abierto fuego. Si tu empiezas, te
        // lo devuelve aunque estes dentro: el DMZ es un refugio, no un parapeto
        // desde el que disparar gratis.
        var m = new TestWorld().WithMap(1_400, 1_400, 700, 700, 1_500)
            .WithNpc(Beast(hp: 100_000, damage: 10, aggro: 500)).Build();
        var p = m.Enter(1, laserDamage: 10, shield: 0,
            data: m.Pilot(1, hp: 100_000, x: 700, y: 700));
        var npc = m.FirstNpc();
        m.Tick();
        Assert.True(p.Last<StationRange>().InRange, "la prueba necesita al jugador en la base");
        p.Clear();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Seconds(4);

        Assert.Contains(p.All<AttackEvent>(), a => a.AttackerId == npc.Id);
    }

    [Fact]
    public void Outside_the_safe_zone_the_same_npc_does_fire()
    {
        // el control del anterior: mismo montaje, sin estacion
        var m = new TestWorld().WithMap(2_000, 2_000, 1, 1, 0)
            .WithNpc(Beast(aggressive: true, aggro: 700, damage: 100)).Build();
        var p = m.Enter(1, shield: 0, data: m.Pilot(1, hp: 100_000, x: 1_000, y: 1_000));

        m.Seconds(6);

        Assert.Contains(p.All<AttackEvent>(), a => a.AttackerId >= 1_000_000);
    }

    // ─── los cobardes ───────────────────────────────────────────────────────

    [Fact]
    public void Below_its_threshold_the_coward_runs_the_other_way()
    {
        var m = WithoutStation().WithNpc(Beast(hp: 1_000, fleesAt: 30, aggressive: true, aggro: 700))
            .Build();
        var p = m.Enter(1, laserDamage: 400, shield: 0, data: m.Pilot(1, hp: 100_000));
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        var (playerX, playerY) = m.Ship(1);
        var distanceBefore = Math.Sqrt(Math.Pow(npc.X - playerX, 2) + Math.Pow(npc.Y - playerY, 2));
        p.Clear();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Seconds(2);                // 400 x 3 golpes deja el casco por debajo del 30%

        Assert.True(npc.Hp * 100 / npc.MaxHp < 30, "la prueba necesita el casco bajo el umbral");
        // no vale mirar el ULTIMO movimiento: el frenazo del golpe se emite
        // despues de la huida dentro del mismo tick. Lo que se afirma es que
        // EXISTE un rumbo que lo aleja, y por mucho (HuidaDistancia son 2500).
        var headings = p.All<EntityMove>().Where(e => e.EntityId == npc.Id)
            .Select(e => Math.Sqrt(Math.Pow((double)e.TargetX - playerX, 2)
                                   + Math.Pow((double)e.TargetY - playerY, 2)))
            .ToList();
        Assert.Contains(headings, d => d > distanceBefore + 1_000);
    }

    [Fact]
    public void A_fleeing_coward_stops_firing()
    {
        var m = WithoutStation().WithNpc(Beast(hp: 1_000, fleesAt: 30, aggressive: true, aggro: 700))
            .Build();
        var p = m.Enter(1, laserDamage: 400, shield: 0, data: m.Pilot(1, hp: 100_000));
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Seconds(2);
        m.W.Post(new LaserToggleCmd(p, false));
        m.Tick();
        p.Clear();

        m.Seconds(8);                // sigue dentro de los 12 s de huida

        Assert.DoesNotContain(p.All<AttackEvent>(), a => a.AttackerId == npc.Id);
    }

    // ─── escudo ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_npc_shield_recovers_after_ten_seconds_of_truce()
    {
        var m = WithoutStation().WithoutNpcCombat()
            .WithNpc(Beast(hp: 100_000, shield: 1_000)).Build();
        var p = m.Enter(1, laserDamage: 500);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        m.W.Post(new LaserToggleCmd(p, false));
        m.Tick();
        Assert.Equal(500u, npc.Shield);

        m.Seconds(9);
        Assert.Equal(500u, npc.Shield);   // todavia dentro de los 10 s de combate

        m.Seconds(4);
        Assert.True(npc.Shield > 500, $"el escudo no regenero: {npc.Shield}");
        Assert.True(npc.Shield <= npc.MaxShield);
    }

    // ─── su muerte ──────────────────────────────────────────────────────────

    [Fact]
    public void On_dying_it_leaves_a_box_credits_and_a_scheduled_respawn()
    {
        var m = WithoutStation().WithNpc(Beast(hp: 100, reward: 250, dropMin: 30, dropMax: 30))
            .Build();
        var p = m.Enter(1, laserDamage: 100);
        var npc = m.FirstNpc();
        var npcId = npc.Id;
        m.MoveNear(p, npc, 100);
        p.Clear();

        m.W.Post(new SelectTargetCmd(p, npcId));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();

        var death = p.Last<EntityDestroyed>();
        Assert.Equal(npcId, death.EntityId);
        Assert.Equal(1ul, death.KillerId);

        var box = p.Last<BoxSpawn>();
        Assert.Equal((ulong)Math.Round(npc.X), box.X);
        Assert.Equal((ulong)Math.Round(npc.Y), box.Y);

        // los credits se asientan SIEMPRE relativos y con su motivo
        Wait.A(() => m.Bd.CreditEntries.Count == 1, "el asiento de credits");
        Assert.Equal((1L, 250m, "NPC_KILL", (long?)npcId), m.Bd.CreditEntries[0]);

        // y vuelve cuando toca: respawn_seconds del catalogo
        Assert.DoesNotContain(npcId, m.W.LiveNpcs.Keys);
        m.Seconds(31);
        Assert.Contains(npcId, m.W.LiveNpcs.Keys);
    }

    [Fact]
    public void Its_death_releases_the_target_of_whoever_killed_it()
    {
        var m = WithoutStation().WithNpc(Beast(hp: 100)).Build();
        var p = m.Enter(1, laserDamage: 100);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
        m.Tick();
        p.Clear();

        m.Seconds(3);
        // el laser se apago solo: sin objetivo no hay mas golpes
        Assert.Empty(p.All<AttackEvent>());
    }
}
