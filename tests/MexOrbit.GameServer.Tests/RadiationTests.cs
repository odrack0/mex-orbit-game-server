// Caracterizacion de la zona radiactiva: mas alla del limite del mapa la nave
// SIGUE volando, pero paga un % del casco maximo por segundo, escalando
// mientras se quede — y el escudo no la salva (World.Radiation.cs).
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class RadiationTests
{
    private static TestWorld Empty() => new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500);

    /// <summary>Nace pegada al limite y vuela a la velocidad TOPE del contrato
    /// (`EntityMove.speed` no pasa de 2000): cruza en unos pocos ticks, no en
    /// uno, pero `TickUntil` no necesita saber cuantos.</summary>
    private static (TestWorld M, FakePort P) EnterAndCross(uint hp = 1_000, uint shield = 0)
    {
        var m = Empty().Build();
        var p = m.Enter(1, shield: shield, data: m.Pilot(1, hp: hp, speed: 2_000, x: 20_700, y: 6_000));
        p.Clear();

        m.W.Post(new MoveIntentCmd(p, 1, 21_500, 6_000));  // 800 u, dentro del margen de 1000
        // un tick primero para que el comando se procese y el destino cambie:
        // `TickUntil` mira la condicion ANTES de tickear, y "parada" es cierto
        // tanto antes de arrancar como al llegar — sin este tick nunca arranca
        m.Tick();
        m.TickUntil(() => !m.W.ShipOf(1)!.Moving, what: "cruza el limite y llega al destino");
        return (m, p);
    }

    [Fact]
    public void Crossing_the_limit_costs_10_percent_of_max_hp_right_away()
    {
        var (m, p) = EnterAndCross(hp: 1_000);

        // el primer golpe pega EN EL MISMO tick que cruza, no un segundo despues
        Assert.Equal(900u, p.Last<HeroStats>().Hp);
    }

    [Fact]
    public void The_near_side_of_the_map_is_radiation_too_not_a_wall()
    {
        // Lo que se reporto en vivo el 1-sep: por el lado del 0 la nave se paraba
        // en seco. No era el clamp de radiacion — eran las coordenadas SIN SIGNO
        // en cinco capas. Ahora x negativo es un destino como cualquier otro.
        var m = Empty().Build();
        var p = m.Enter(1, data: m.Pilot(1, hp: 1_000, speed: 2_000, x: 100, y: 6_000));
        p.Clear();

        m.W.Post(new MoveIntentCmd(p, 1, -700, 6_000));   // 800 u, dentro del margen
        m.Tick();
        m.TickUntil(() => !m.W.ShipOf(1)!.Moving, what: "cruza el 0 y llega");

        Assert.Equal(-700L, p.Last<EntityMove>().TargetX);   // el eco tambien va con signo
        Assert.Equal(-700.0, m.W.ShipOf(1)!.X);
        Assert.Equal(900u, p.Last<HeroStats>().Hp);          // y cobra igual que el otro lado
    }

    [Fact]
    public void Staying_in_the_zone_escalates_one_point_per_second()
    {
        var (m, p) = EnterAndCross(hp: 1_000);
        Assert.Equal(900u, p.Last<HeroStats>().Hp);   // segundo 1: 10%

        m.Seconds(1);
        Assert.Equal(790u, p.Last<HeroStats>().Hp);   // segundo 2: 11% de 1000 = 110

        m.Seconds(1);
        Assert.Equal(670u, p.Last<HeroStats>().Hp);   // segundo 3: 12% de 1000 = 120
    }

    [Fact]
    public void Radiation_skips_the_shield_entirely()
    {
        var (m, p) = EnterAndCross(hp: 1_000, shield: 500);

        var stats = p.Last<HeroStats>();
        Assert.Equal(900u, stats.Hp);      // se cobro del casco...
        Assert.Equal(500u, stats.Shield);  // ...el escudo ni se entero
    }

    [Fact]
    public void Coming_back_inside_resets_the_escalation()
    {
        var (m, p) = EnterAndCross(hp: 1_000);
        m.Seconds(1);
        Assert.Equal(790u, p.Last<HeroStats>().Hp);   // ya en el 11%

        // vuelve dentro del limite (un tick para que el comando arranque el
        // movimiento antes de que "parado" pueda significar "todavia no salio")
        m.W.Post(new MoveIntentCmd(p, 2, 19_000, 6_000));
        m.Tick();
        m.TickUntil(() => !m.W.ShipOf(1)!.Moving, what: "llega de vuelta dentro del limite");
        m.Seconds(2);   // de sobra: dentro del limite no debe pasar nada mas

        var dentro = p.Last<HeroStats>().Hp;
        Assert.Equal(790u, dentro);   // sin mas daño mientras esta dentro

        // y al volver a salir, el primer golpe es otra vez el 10% inicial, no
        // una continuacion del 12%
        m.W.Post(new MoveIntentCmd(p, 3, 21_500, 6_000));
        m.TickUntil(() => Geometry.OutsideBounds(m.W.ShipOf(1)!.X, m.W.ShipOf(1)!.Y, m.Map),
            what: "vuelve a cruzar el limite");

        Assert.Equal(dentro - 100u, p.Last<HeroStats>().Hp);   // 10% de 1000, no 120
    }

    [Fact]
    public void Enough_seconds_in_the_zone_kill_the_pilot_by_radiation()
    {
        var (m, p) = EnterAndCross(hp: 100);

        m.Seconds(15);   // de sobra: 10+11+12+...+17 ya la mata antes del octavo segundo

        Assert.Equal(0u, p.Last<HeroStats>().Hp);

        var death = p.Last<EntityDestroyed>();
        Assert.Equal(1ul, death.EntityId);
        Assert.Equal(1ul, death.KillerId);   // se la cobra ella misma: no hay agresor

        var opciones = p.Last<RespawnOptions>();
        Assert.Equal(DeathCause.Radiation, opciones.Cause);
    }

    [Fact]
    public void Once_dead_radiation_stops_hitting_it()
    {
        var (m, p) = EnterAndCross(hp: 100);
        m.Seconds(15);
        Assert.Equal(0u, p.Last<HeroStats>().Hp);
        p.Clear();

        m.Seconds(3);

        Assert.False(p.Received<HeroStats>());
    }
}
