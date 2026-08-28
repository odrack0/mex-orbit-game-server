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
    public void A_hit_does_not_freeze_the_npc_it_makes_it_come_at_you()
    {
        // Aqui vivia lo contrario: el golpe frenaba al bicho en seco. Entro cuando
        // todavia no habia IA —un bicho golpeado seguia paseando hasta salirse del
        // alcance— y cinco horas despues `FightBack` resolvio eso por el buen
        // camino. Desde entonces el frenazo cancelaba la persecucion que acababa
        // de empezar, y como `Approach` ya habia dejado el estado en
        // `WaitingForPrey` —que no vuelve a emitir destino— el bicho se quedaba
        // plantado donde le pillo el primer disparo. Se veia como "no me persigue".
        var m = WithoutStation()
            // aggro 500 como el Vex real: `LostPrey` mide el desaggro contra ESE radio,
            // asi que con 0 el bicho olvida a quien le dispara en el acto
            .WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 100_000, 0, 200, 10, false, 0, 500,
                30, 1, 0, 0, 0, 0, 0))
            .Build();
        var npc = m.FirstNpc();
        // el jugador se coloca RESPECTO al bicho: dentro del laser (600) y fuera
        // de su circulo de aproximacion (300), que es donde se puede medir si cierra
        var x = Math.Clamp(npc.X + 550, 0, 20_800);
        var p = m.Enter(1, laserDamage: 10,
            data: m.Pilot(1, x: (uint)Math.Round(x), y: (uint)Math.Round(npc.Y)));
        var before = Geometry.Distance(npc.X, npc.Y, x, npc.Y);

        Fire(m, p, npc);
        m.Seconds(6);

        var after = Geometry.Distance(npc.X, npc.Y, x, npc.Y);
        Assert.True(after < 400,
            $"se quedo plantado: de {before:F0} a {after:F0} u mientras le disparaban");
    }

    [Fact]
    public void Sustained_fire_does_not_make_the_npc_dance_around_the_ship()
    {
        // `FightBack` reiniciaba la aproximacion en cada golpe: con el laser
        // pegando cada 500 ms, el bicho re-elegia un punto del circulo CADA
        // pensamiento y bailoteaba alrededor de la nave sin plantarse nunca. El
        // legado no tocaba la maquina de estados al recibir un golpe, y esa
        // diferencia era ademas la fuente de los tirones en el cliente: cada
        // rumbo nuevo le obligaba a girar con el avance frenado mientras el
        // server volaba recto.
        var m = WithoutStation()
            .WithNpc(new NpcSpawnInfo(1, "vex", "Vex", 100_000, 0, 200, 10, false, 0, 500,
                30, 1, 0, 0, 0, 0, 0))
            .Build();
        var npc = m.FirstNpc();
        var x = Math.Clamp(npc.X + 400, 0, 20_800);
        var p = m.Enter(1, laserDamage: 10,
            data: m.Pilot(1, x: (uint)Math.Round(x), y: (uint)Math.Round(npc.Y)));
        p.Clear();

        Fire(m, p, npc);
        m.Seconds(8);

        // el tramo hasta el circulo y como mucho un reajuste; uno por segundo
        // seria el bailoteo de vuelta
        var headings = p.All<EntityMove>().Count(e => e.EntityId == npc.Id);
        Assert.InRange(headings, 1, 3);
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
