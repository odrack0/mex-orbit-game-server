// Caracterizacion del combate jugador -> NPC. Fija lo que HOY hace el juego,
// para que el refactor por capas no lo cambie sin que nadie se entere.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class CombateTests
{
    /// <summary>Mapa sin estacion: el DMZ tiene sus propias pruebas y aqui solo
    /// estorbaria (dentro de la zona segura no hay combate).</summary>
    private static Mundo SinEstacion() => new Mundo().ConMapa(20_800, 12_800, 1, 1, 0);

    [Fact]
    public void Seleccionar_objetivo_devuelve_sus_dos_barras()
    {
        var m = SinEstacion().ConManiqui(hp: 1_000, escudo: 500).Construir();
        var p = m.Entrar(1);
        var npc = m.PrimerNpc();

        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.Tick();

        var info = p.Ultimo<TargetInfo>();
        Assert.Equal(npc.Id, info.EntityId);
        Assert.Equal(1_000u, info.Hp);
        Assert.Equal(1_000u, info.MaxHp);
        Assert.Equal(500u, info.Shield);
        Assert.Equal(500u, info.MaxShield);
    }

    [Fact]
    public void El_escudo_absorbe_antes_que_el_casco()
    {
        var m = SinEstacion().ConManiqui(hp: 1_000, escudo: 500).Construir();
        var p = m.Entrar(1, danioLaser: 100);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        p.Limpiar();

        Disparar(m, p, npc);
        m.Tick();

        var golpe = p.Ultimo<AttackEvent>();
        Assert.Equal(100u, golpe.Damage);
        // los valores del evento son POST-daño, siempre
        Assert.Equal(400u, golpe.TargetShield);
        Assert.Equal(1_000u, golpe.TargetHp);
        Assert.Equal(400u, npc.Shield);
        Assert.Equal(1_000u, npc.Hp);
    }

    [Fact]
    public void Un_golpe_mayor_que_el_escudo_desborda_al_casco()
    {
        var m = SinEstacion().ConManiqui(hp: 1_000, escudo: 500).Construir();
        var p = m.Entrar(1, danioLaser: 600);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        p.Limpiar();

        Disparar(m, p, npc);
        m.Tick();

        var golpe = p.Ultimo<AttackEvent>();
        Assert.Equal(0u, golpe.TargetShield);
        Assert.Equal(900u, golpe.TargetHp);   // 600 - 500 de escudo = 100 al casco
    }

    [Fact]
    public void La_cadencia_es_de_un_golpe_cada_500_ms()
    {
        var m = SinEstacion().ConManiqui(hp: 100_000, escudo: 0).Construir();
        var p = m.Entrar(1, danioLaser: 10);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        p.Limpiar();

        Disparar(m, p, npc);
        // solo los golpes DEL JUGADOR: al maniqui, aunque sea pasivo, pegarle lo
        // convierte en agresor y sus disparos ensucian la cuenta
        m.Tick();                     // el primer golpe sale en el tick del toggle
        Assert.Single(Golpes(p));

        m.Tick(5);                    // 5 ticks mas = 480 ms: todavia no toca
        Assert.Single(Golpes(p));

        m.Tick();                     // el sexto tick completa los 500 ms
        Assert.Equal(2, Golpes(p).Count);
    }

    private static List<AttackEvent> Golpes(PuertoFalso p) =>
        p.Todos<AttackEvent>().Where(a => a.AttackerId == (ulong)p.AccountId).ToList();

    [Fact]
    public void Fuera_de_alcance_el_laser_espera_en_vez_de_apagarse()
    {
        var m = SinEstacion().ConManiqui(hp: 100_000, escudo: 0).Construir();
        var p = m.Entrar(1, danioLaser: 10);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 700);       // LaserRange son 600
        p.Limpiar();

        Disparar(m, p, npc);
        m.Segundos(3);
        Assert.Empty(p.Todos<AttackEvent>());

        // sin volver a encenderlo: acercarse basta para que empiece a pegar
        m.Acercar(p, npc, 300);
        m.Tick();
        Assert.NotEmpty(p.Todos<AttackEvent>());
    }

    [Fact]
    public void El_laser_no_se_enciende_sin_objetivo()
    {
        var m = SinEstacion().ConManiqui().Construir();
        var p = m.Entrar(1);

        m.W.Post(new LaserToggleCmd(p, true));
        m.Segundos(2);

        Assert.Empty(p.Todos<AttackEvent>());
    }

    [Fact]
    public void El_golpe_frena_al_NPC_en_seco()
    {
        // mapa diminuto: el bicho vagabundea pero jamas sale del alcance del
        // laser. Con el mapa grande, volar hasta el le daba tiempo de largarse
        // a 3000 unidades y el golpe no llegaba a producirse.
        var m = new Mundo().ConMapa(1_200, 1_200, 1, 1, 0)
            .ConNpc(new NpcSpawnInfo(1, "vex", "Vex", 10_000, 0, 200, 50, false, 0, 0,
                30, 1, 0, 0, 0, 0, 0))
            .Construir();
        var p = m.Entrar(1, danioLaser: 10, datos: m.Piloto(1, x: 600, y: 600));
        var npc = m.PrimerNpc();
        // el rumbo lo elige el sorteo, asi que se espera a que arranque de verdad
        m.TickHasta(() => npc.Moving, que: "que el NPC eche a andar");

        Disparar(m, p, npc);
        m.Tick();

        // el golpe lo planta donde este
        Assert.False(npc.Moving);
        Assert.Equal(npc.X, npc.TargetX);
        Assert.Equal(npc.Y, npc.TargetY);
    }

    [Fact]
    public void El_disparo_viaja_con_la_municion_equipada()
    {
        var m = SinEstacion().ConManiqui(hp: 10_000, escudo: 0).Construir();
        var p = m.Entrar(1, danioLaser: 10);
        var npc = m.PrimerNpc();
        m.Acercar(p, npc, 100);
        p.Limpiar();

        Disparar(m, p, npc);
        m.Tick();

        var golpe = p.Ultimo<AttackEvent>();
        Assert.Equal("ammo_cel_1", golpe.AmmoId);
        Assert.False(golpe.Skilled);
        Assert.False(golpe.Missed);
        Assert.Equal(Weapon.Laser, golpe.Weapon);
    }

    private static void Disparar(Mundo m, PuertoFalso p, Entity npc)
    {
        m.W.Post(new SelectTargetCmd(p, npc.Id));
        m.W.Post(new LaserToggleCmd(p, true));
    }
}
