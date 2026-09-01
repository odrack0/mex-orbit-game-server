// Caracterizacion del resto del mundo: movimiento autoritativo, salto de sector,
// chat, muerte del jugador y la promesa de que ningun fallo tumba el loop.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Tests;

public class MovementTests
{
    private static TestWorld Empty() => new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500);

    [Fact]
    public void The_destination_is_clamped_far_beyond_the_map_where_nobody_arrives_alive()
    {
        var m = Empty().Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new MoveIntentCmd(p, 1, 999_999, 999_999));
        m.Tick();

        // el Moving eterno del legado, imposible: el clamp es del servidor. Pero
        // NO al limite del mapa: mas alla esta la zona radiactiva y la nave
        // sigue hasta explotar (RadiationTests). El tope es estructural
        // (Dials.RadiationReach, 50 000 mas alla) — no una pared que se sienta
        var eco = p.Last<EntityMove>();
        Assert.Equal(70_800L, eco.TargetX);
        Assert.Equal(62_800L, eco.TargetY);
    }

    [Fact]
    public void On_the_near_side_the_clamp_is_MINUS_the_reach_not_zero()
    {
        // Las coordenadas van con signo desde el 1-sep: antes cinco capas
        // (cliente, wire uint, Round, este clamp y la BD) dejaban el lado del 0
        // en pared y la radiacion solo existia por la derecha y por abajo.
        var m = Empty().Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new MoveIntentCmd(p, 1, -999_999, -999_999));
        m.Tick();

        var eco = p.Last<EntityMove>();
        Assert.Equal(-50_000L, eco.TargetX);
        Assert.Equal(-50_000L, eco.TargetY);
    }

    [Fact]
    public void A_stale_intent_is_dropped_without_drama()
    {
        var m = Empty().Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new MoveIntentCmd(p, 5, 11_000, 6_000));
        m.W.Post(new MoveIntentCmd(p, 3, 9_000, 6_000));
        m.Tick();

        var ecos = p.All<EntityMove>().Where(e => e.EntityId == 1).ToList();
        Assert.Single(ecos);
        Assert.Equal(11_000L, ecos[0].TargetX);
    }

    [Fact]
    public void The_authoritative_echo_reaches_everyone_including_the_hero()
    {
        var m = Empty().Build();
        var heroe = m.Enter(1);
        var other = m.Enter(2);
        heroe.Clear();
        other.Clear();

        m.W.Post(new MoveIntentCmd(heroe, 1, 11_000, 6_000));
        m.Tick();

        Assert.Contains(heroe.All<EntityMove>(), e => e.EntityId == 1);
        Assert.Contains(other.All<EntityMove>(), e => e.EntityId == 1);
    }
}

public class JumpTests
{
    private static TestWorld WithPortal(bool funciona = true) =>
        new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500)
            .WithPortal(new PortalInfo(7, 10_500, 6_000, "1-2", funciona, 500, 500));

    [Fact]
    public void Next_to_a_valid_portal_the_jump_is_negotiated_upstairs()
    {
        var m = WithPortal().Build();
        (long Account, PortalInfo Portal)? pedido = null;
        m.W.Jump += (_, account, portal) => pedido = (account, portal);
        var p = m.Enter(1);   // entra en 10000,6000: a 500 del portal

        m.W.Post(new JumpCmd(p, 9, 7));
        m.Tick();

        Assert.NotNull(pedido);
        Assert.Equal(1L, pedido!.Value.Account);
        Assert.Equal("1-2", pedido.Value.Portal.TargetMapCode);
    }

    [Fact]
    public void A_portal_that_does_not_exist_in_this_map_answers_GONE()
    {
        var m = WithPortal().Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new JumpCmd(p, 9, 999));
        m.Tick();

        var error = p.Last<ErrorReply>();
        Assert.Equal(ErrorCode.Gone, error.Code);
        Assert.Equal(9ul, error.RequestId);
    }

    [Fact]
    public void An_inactive_portal_answers_INVALID()
    {
        var m = WithPortal(funciona: false).Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new JumpCmd(p, 9, 7));
        m.Tick();

        Assert.Equal(ErrorCode.Invalid, p.Last<ErrorReply>().Code);
    }

    [Fact]
    public void Far_from_the_portal_it_answers_TOO_FAR_however_much_the_client_insists()
    {
        var m = WithPortal().Build();
        var p = m.Enter(1);
        m.MoveTo(p, 12_000, 6_000);   // JumpRange son 600
        p.Clear();

        m.W.Post(new JumpCmd(p, 9, 7));
        m.Tick();

        // el cliente propone, el server dispone (y el cliente puede mentir)
        Assert.Equal(ErrorCode.TooFar, p.Last<ErrorReply>().Code);
    }
}

public class ChatTests
{
    private static TestWorld Empty() => new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500);

    [Fact]
    public void The_global_channel_reaches_everyone_including_the_speaker()
    {
        var m = Empty().Build();
        var uno = m.Enter(1, data: m.Pilot(1, "Ana", faction: 1));
        var dos = m.Enter(2, data: m.Pilot(2, "Beto", faction: 2));
        uno.Clear();
        dos.Clear();

        m.W.Post(new ChatSendCmd(uno, 1, ChatChannel.Global, "hola sector"));
        m.Tick();

        Assert.Equal("hola sector", uno.Last<ChatMessage>().Text);
        Assert.Equal("Ana", dos.Last<ChatMessage>().FromName);
    }

    [Fact]
    public void The_faction_channel_does_not_cross_factions()
    {
        var m = Empty().Build();
        var uno = m.Enter(1, data: m.Pilot(1, "Ana", faction: 1));
        var mismo = m.Enter(2, data: m.Pilot(2, "Ada", faction: 1));
        var other = m.Enter(3, data: m.Pilot(3, "Beto", faction: 2));
        mismo.Clear();
        other.Clear();

        m.W.Post(new ChatSendCmd(uno, 1, ChatChannel.Faction, "solo los nuestros"));
        m.Tick();

        Assert.True(mismo.Received<ChatMessage>());
        Assert.False(other.Received<ChatMessage>());
    }

    [Fact]
    public void A_long_message_is_truncated_to_256()
    {
        var m = Empty().Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new ChatSendCmd(p, 1, ChatChannel.Global, new string('x', 400)));
        m.Tick();

        Assert.Equal(256, p.Last<ChatMessage>().Text.Length);
    }

    [Fact]
    public void An_empty_message_does_not_travel()
    {
        var m = Empty().Build();
        var p = m.Enter(1);
        p.Clear();

        m.W.Post(new ChatSendCmd(p, 1, ChatChannel.Global, "   "));
        m.Tick();

        Assert.False(p.Received<ChatMessage>());
    }
}

public class PlayerDeathTests
{
    private static (TestWorld M, FakePort P, Entity Npc) Cornered(
        Dictionary<long, uint>? cargo = null)
    {
        var m = new TestWorld().WithMap(20_800, 12_800, 100, 100, 50)
            .WithNpc(new NpcSpawnInfo(1, "ferox", "Ferox", 100_000, 0, 0, 100, true, 0, 2_000,
                30, 1, 0, 0, 0, 0, 0))
            .Build();
        var p = m.Enter(1, shield: 0, cargo: cargo, data: m.Pilot(1, hp: 150));
        var npc = m.FirstNpc();
        m.MoveNear(p, npc, 100);
        return (m, p, npc);
    }

    [Fact]
    public void On_dying_destruction_is_announced_and_respawn_is_offered()
    {
        // sin Clear: el bicho ya dispara mientras el jugador se acerca, asi que
        // la muerte puede caer antes de que la prueba llegue a mirar
        var (m, p, npc) = Cornered();

        m.Seconds(5);

        var death = p.Last<EntityDestroyed>();
        Assert.Equal(1ul, death.EntityId);
        Assert.Equal(npc.Id, death.KillerId);
        Assert.Equal(0u, p.Last<HeroStats>().Hp);

        var opciones = p.Last<RespawnOptions>();
        Assert.Equal(DeathCause.Npc, opciones.Cause);
        Assert.Equal("Ferox", opciones.KillerName);
        var unica = Assert.Single(opciones.Options);
        Assert.Equal(1ul, unica.OptionId);
        Assert.Equal(0ul, unica.CostCredits);
        Assert.True(unica.Available);
    }

    [Fact]
    public void The_flying_hold_stays_put_inside_a_box()
    {
        var (m, p, _) = Cornered(new Dictionary<long, uint> { [10] = 42 });

        m.Seconds(5);

        // transferencia, no destruccion (guidelines §7)
        Assert.True(p.Received<BoxSpawn>());
        Wait.A(() => m.Bd.ClearedCargo.Count == 1, "el asiento CARGO_LOST");
        Assert.Equal(1L, m.Bd.ClearedCargo[0].AccountId);
        Assert.Equal((long)p.Last<BoxSpawn>().BoxId, m.Bd.ClearedCargo[0].BoxRef);
    }

    [Fact]
    public void With_no_cargo_no_box_is_left()
    {
        var (m, p, _) = Cornered();

        m.Seconds(5);

        Assert.False(p.Received<BoxSpawn>());
        Assert.Empty(m.Bd.ClearedCargo);
    }

    [Fact]
    public void While_destroyed_it_does_not_fly()
    {
        var (m, p, _) = Cornered();
        m.Seconds(5);
        p.Clear();

        m.W.Post(new MoveIntentCmd(p, 99, 5_000, 5_000));
        m.Tick();

        Assert.Empty(p.All<EntityMove>().Where(e => e.EntityId == 1));
    }

    [Fact]
    public void Respawning_returns_the_ship_whole_to_the_station()
    {
        var (m, p, _) = Cornered();
        m.Seconds(5);
        p.Clear();

        m.W.Post(new RespawnSelectCmd(p, 1));
        m.Tick();

        var ship = p.All<EntitySpawn>().Last(e => e.EntityId == 1);
        Assert.Equal(100L, ship.X);           // la estacion del mapa
        Assert.Equal(100L, ship.Y);
        Assert.Equal(1f, ship.HpPct);
        Assert.Equal(150u, p.Last<HeroStats>().Hp);
    }
}

public class ResilienceTests
{
    [Fact]
    public void A_failure_while_persisting_does_not_kill_the_loop()
    {
        // la leccion del TickManager legado: una excepcion jamas mata el bucle
        var m = new TestWorld().WithMap(20_800, 12_800, 10_000, 6_000, 1_500).Build();
        var p = m.Enter(1);
        m.Bd.FailsOnSave = true;

        m.Seconds(35);               // dispara el write-behind (cada 30 s)
        m.Bd.FailsOnSave = false;
        p.Clear();

        // el mundo sigue atendiendo comandos despues del desastre
        m.W.Post(new MoveIntentCmd(p, 1, 11_000, 6_000));
        m.Tick();

        Assert.Contains(p.All<EntityMove>(), e => e.EntityId == 1);
    }
}
