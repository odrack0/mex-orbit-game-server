// Caracterizacion del combate jugador -> NPC. Fija lo que HOY hace el juego,
// para que el refactor por capas no lo cambie sin que nadie se entere.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class CombatTests
{
    /// <summary>Mapa sin estacion: el DMZ tiene sus propias pruebas y aqui solo
    /// estorbaria (dentro de la zona segura no hay combate).</summary>
    private static TestWorld WithoutStation() => new TestWorld().WithMap(20_800, 12_800, 1, 1, 0);

    [Fact]
    public void Selecting_a_target_returns_its_two_bars()
    {
        var m = WithoutStation().WithDummy(hp: 1_000, shield: 500).Build();
        var p = m.Enter(1);
        var npc = m.FirstNpc();
        // hay que VERLO para poder ficharlo: la relevancia por rango tiene sus
        // propias pruebas, aqui solo interesa que llegan las dos barras
        m.MoveNear(p, npc, 100);

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();

        var info = p.Last<TargetInfo>();
        Assert.Equal(npc.Id, info.EntityId);
        Assert.Equal(1_000u, info.Hp);
        Assert.Equal(1_000u, info.MaxHp);
        Assert.Equal(500u, info.Shield);
        Assert.Equal(500u, info.MaxShield);
    }

    [Fact]
    public void The_shield_absorbs_before_the_hull()
    {
        var m = WithoutStation().WithDummy(hp: 1_000, shield: 500).Build();
        var p = m.Enter(1, laserDamage: 100);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        p.Clear();

        Fire(m, p, npc);
        m.Tick();

        var hit = p.Last<AttackEvent>();
        Assert.Equal(100u, hit.Damage);
        // los valores del evento son POST-daño, siempre
        Assert.Equal(400u, hit.TargetShield);
        Assert.Equal(1_000u, hit.TargetHp);
        Assert.Equal(400u, npc.Shield);
        Assert.Equal(1_000u, npc.Hp);
    }

    [Fact]
    public void A_hit_bigger_than_the_shield_spills_into_the_hull()
    {
        var m = WithoutStation().WithDummy(hp: 1_000, shield: 500).Build();
        var p = m.Enter(1, laserDamage: 600);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        p.Clear();

        Fire(m, p, npc);
        m.Tick();

        var hit = p.Last<AttackEvent>();
        Assert.Equal(0u, hit.TargetShield);
        Assert.Equal(900u, hit.TargetHp);   // 600 - 500 de escudo = 100 al casco
    }

    [Fact]
    public void The_rate_of_fire_is_one_hit_every_500_ms()
    {
        var m = WithoutStation().WithDummy(hp: 100_000, shield: 0).Build();
        var p = m.Enter(1, laserDamage: 10);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        p.Clear();

        Fire(m, p, npc);
        // solo los golpes DEL JUGADOR: al maniqui, aunque sea pasivo, pegarle lo
        // convierte en agresor y sus disparos ensucian la cuenta
        m.Tick();                     // el primer golpe sale en el tick del toggle
        Assert.Single(Shots(p));

        m.Tick(5);                    // 5 ticks mas = 480 ms: todavia no toca
        Assert.Single(Shots(p));

        m.Tick();                     // el sexto tick completa los 500 ms
        Assert.Equal(2, Shots(p).Count);
    }

    private static List<AttackEvent> Shots(FakePort p) =>
        p.All<AttackEvent>().Where(a => a.AttackerId == (ulong)p.AccountId).ToList();

    [Fact]
    public void Out_of_range_the_laser_waits_instead_of_switching_off()
    {
        var m = WithoutStation().WithDummy(hp: 100_000, shield: 0).Build();
        var p = m.Enter(1, laserDamage: 10);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 700);       // LaserRange son 600
        p.Clear();

        Fire(m, p, npc);
        m.Seconds(3);
        Assert.Empty(p.All<AttackEvent>());

        // sin volver a encenderlo: acercarse basta para que empiece a pegar
        m.MoveNear(p, npc, 300);
        m.Tick();
        Assert.NotEmpty(p.All<AttackEvent>());
    }

    [Fact]
    public void Waiting_out_of_range_is_announced_ONCE()
    {
        // la pantalla mide 2198x1159 y el laser alcanza 600: mas de la mitad de lo
        // que se VE esta fuera de tiro, y esperar en silencio se siente como que
        // el disparo no funciona
        var m = WithoutStation().WithDummy(hp: 100_000, shield: 0).Build();
        var p = m.Enter(1, laserDamage: 10);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 900);            // visible (2000) pero fuera de tiro (600)
        p.Clear();

        Fire(m, p, npc);
        m.Seconds(3);

        var warning = Assert.Single(p.All<ErrorReply>());
        Assert.Equal(ErrorCode.TooFar, warning.Code);
        Assert.Equal(0ul, warning.RequestId);   // no responde a nada: lo cuenta el server
        Assert.Empty(p.All<AttackEvent>());

        // y al ponerse a tiro dispara sin volver a encender nada
        m.MoveNear(p, npc, 300);
        m.Tick();
        Assert.NotEmpty(p.All<AttackEvent>());
    }

    [Fact]
    public void The_laser_does_not_switch_on_without_a_target()
    {
        var m = WithoutStation().WithDummy().Build();
        var p = m.Enter(1);

        m.W.Post(new LaserToggleCmd(p, true));
        m.Seconds(2);

        Assert.Empty(p.All<AttackEvent>());
    }

    [Fact]
    public void A_hit_stops_the_npc_dead()
    {
        // mapa diminuto: el bicho vagabundea pero jamas sale del alcance del
        // laser. Con el mapa grande, volar hasta el le daba tiempo de largarse
        // a 3000 unidades y el golpe no llegaba a producirse.
        var m = new TestWorld().WithMap(1_200, 1_200, 1, 1, 0)
            .WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 10_000, 0, 200, 50, false, 0, 0,
                30, 1, 0, 0, 0, 0, 0))
            .Build();
        var p = m.Enter(1, laserDamage: 10, data: m.Pilot(1, x: 600, y: 600));
        var npc = m.FirstNpc();
        // el rumbo lo elige el sorteo, asi que se espera a que arranque de verdad
        m.TickUntil(() => npc.Moving, what: "que el NPC eche a andar");

        Fire(m, p, npc);
        m.Tick();

        // el golpe lo planta donde este
        Assert.False(npc.Moving);
        Assert.Equal(npc.X, npc.TargetX);
        Assert.Equal(npc.Y, npc.TargetY);
    }

    [Fact]
    public void The_shot_travels_with_the_equipped_ammo()
    {
        var m = WithoutStation().WithDummy(hp: 10_000, shield: 0).Build();
        var p = m.Enter(1, laserDamage: 10);
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        p.Clear();

        Fire(m, p, npc);
        m.Tick();

        var hit = p.Last<AttackEvent>();
        Assert.Equal("ammo_cel_1", hit.AmmoId);
        Assert.False(hit.Skilled);
        Assert.False(hit.Missed);
        Assert.Equal(Weapon.Laser, hit.Weapon);
    }

    private static void Fire(TestWorld m, FakePort p, Entity npc)
    {
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
    }
}
