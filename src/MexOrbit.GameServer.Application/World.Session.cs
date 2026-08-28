// Entrar, volver, salir, moverse, hablar y saltar de sector.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    private void OnJoin(JoinCmd join)
    {
        // sesion unica: la conexion nueva expulsa a la vieja, avisando (nunca silencio)
        if (_players.TryGetValue(join.Player.AccountId, out var previous))
        {
            Send(previous, new SessionTakenOver());
            previous.Port.CloseSocket();
            Despawn(previous.Entity.Id, DespawnReason.Left);
            _players.Remove(join.Player.AccountId);
        }

        var hero = new Entity
        {
            Id = (ulong)join.Player.AccountId,       // convencion: jugador = account_id
            Kind = EntityKind.Player,
            TypeId = join.Player.ShipCode,
            Name = join.Player.PilotName,
            Faction = join.Player.Faction,
            Speed = join.Player.BaseSpeed,
            Hp = join.Player.CurrentHp,
            MaxHp = join.Player.BaseHp,
            // el escudo del casco + sus generadores. En E2 se entra con el escudo
            // LLENO (salir de la base lo recarga): la regeneracion en vuelo aun no
            // existe, y arrastrar un 0 guardado dejaria al jugador sin escudo para
            // siempre. Se persiste igual, para cuando la regeneracion llegue.
            Shield = join.MaxShield,
            MaxShield = join.MaxShield,
            X = join.Player.PosX,
            Y = join.Player.PosY,
        };
        hero.Stop();

        var slot = new PlayerSlot
        {
            Port = join.Port, Entity = hero, Data = join.Player,
            SessionId = join.SessionId, LastPingTick = _tick,
            LaserDamage = join.LaserDamage, Cargo = join.Cargo,
            Credits = join.Player.Credits,
        };

        _players[join.Player.AccountId] = slot;
        SyncWorld(slot);
        // los demas lo ven llegar por relevancia, en este mismo tick
        log.LogInformation("cuenta {id} ({nombre}) entro al mapa {code}",
            join.Player.AccountId, join.Player.PilotName, map.Code);
    }

    /// <summary>El mundo que le toca a un jugador al entrar y al reconectar.
    ///
    /// Ya NO es el mapa entero. El mobiliario —limites, estacion, portales,
    /// precios— si viaja completo, porque es del mapa y no de nadie; las
    /// entidades y las cajas entran por relevancia, asi que se manda lo que este
    /// en rango AHORA y el resto va llegando conforme se vuela hacia ello.</summary>
    private void SyncWorld(PlayerSlot slot)
    {
        // los portales van completos aqui: son mobiliario del mapa, no entidades
        Send(slot, new MapEntered(map, portals, CargoRiskPct: 100));
        Send(slot, new PricesPublished(npcPrices));
        Send(slot, new EntitySpawned(slot.Entity));
        Send(slot, HeroStatsOf(slot));
        SeedRelevance(slot);
        SendStorage(slot);
    }

    /// <summary>Volver. Hay DOS formas de volver y las dos entran por aqui:
    ///
    ///   · Se cayo el socket y la nave sigue en este mapa, dentro de la ventana de
    ///     gracia. El slot existe: el socket nuevo toma el relevo.
    ///   · Se llega de OTRO mapa (o de otro servidor) tras un salto. Aqui nadie ha
    ///     visto nunca a este jugador, y eso NO es un error: es exactamente lo que
    ///     pasa al cruzar un portal. Se entra de cero, con lo que diga la BD — que
    ///     ya dice el mapa y la posicion, porque el origen los persistio antes de
    ///     soltarlo.
    ///
    /// Antes solo existia el primer caso y el segundo respondia RESUME_EXPIRED, asi
    /// que el salto llegaba al server, persistia... y dejaba al jugador fuera.</summary>
    private void OnResume(ResumeCmd cmd)
    {
        if (!_players.TryGetValue(cmd.AccountId, out var slot))
        {
            if (cmd.Player is null)
            {
                Send(cmd.Port, new Failed(0, ErrorCode.ResumeExpired));
                cmd.Port.CloseSocket();
                return;
            }
            Send(cmd.Port, new ResumeAccepted());
            OnJoin(new JoinCmd(cmd.Port, cmd.Player, cmd.SessionId, cmd.LaserDamage,
                cmd.MaxShield, cmd.Cargo ?? []));
            return;
        }
        slot.Port = cmd.Port;                    // el socket nuevo toma el relevo
        slot.GraceUntilTick = long.MaxValue;
        slot.PingMisses = 0;
        slot.LastPingTick = _tick;

        Send(slot, new ResumeAccepted());
        // re-sincronizacion completa: estado del mundo tal como esta ahora
        SyncWorld(slot);
        log.LogInformation("cuenta {id} reconecto dentro de la gracia", cmd.AccountId);
    }

    private void OnLeave(LeaveCmd leave)
    {
        var slot = SlotOf(leave.Port);
        if (slot is null) return;
        // LOGOUT explicito = se va de verdad; una caida de socket abre la ventana
        // de gracia y la nave se queda en el mundo (auth-v1)
        if (leave.Reason == "LOGOUT")
        {
            Drop(slot, leave.Reason);
            return;
        }
        if (slot.Disconnected) return;
        slot.GraceUntilTick = _tick + ToTicks(Dials.GraceMs);
        slot.LaserOn = false;
        log.LogInformation("cuenta {id}: socket caido, {s} s de gracia para reconectar",
            slot.Data.AccountId, Dials.GraceMs / 1000);
    }

    private void Drop(PlayerSlot slot, string reason)
    {
        _players.Remove(slot.Data.AccountId);
        Despawn(slot.Entity.Id, DespawnReason.Left);
        slot.Port.CloseSocket();
        var (id, mapId, x, y, hp, esc, sid) = (slot.Data.AccountId, map.Id,
            (uint)slot.Entity.X, (uint)slot.Entity.Y, slot.Entity.Hp, slot.Entity.Shield,
            slot.SessionId);
        _ = Task.Run(() => Safe(() =>
        {
            players.SaveShipState(id, mapId, x, y, hp, esc);   // el estado siempre se persiste al salir
            sessions.CloseSession(sid, reason);
        }, "Drop"));
        log.LogInformation("cuenta {id} salio ({reason})", id, reason);
    }

    // ─── movimiento y latido ────────────────────────────────────────────────

    private void OnMoveIntent(MoveIntentCmd move)
    {
        var slot = SlotOf(move.Port);
        if (slot is null) return;
        if (slot.Dead) return;
        // seq monotona: lo viejo o duplicado se descarta sin drama
        if (move.Seq <= slot.LastSeq) return;
        slot.LastSeq = move.Seq;
        // clamp server-side a los limites del mapa: el Moving eterno del legado, imposible
        slot.Entity.TargetX = Math.Clamp(move.TargetX, 0, map.BoundsX);
        slot.Entity.TargetY = Math.Clamp(move.TargetY, 0, map.BoundsY);
        // eco autoritativo a TODOS, heroe incluido: contra esto se reconcilia el cliente
        ToThoseWhoSee(slot.Entity.Id, new EntityMoved(slot.Entity));
    }

    private void OnPong(PongCmd pong)
    {
        var slot = SlotOf(pong.Port);
        if (slot is null || pong.Nonce != slot.PingNonce) return;
        slot.PingMisses = 0;
    }

    // ─── chat ───────────────────────────────────────────────────────────────

    private void OnChatSend(ChatSendCmd cmd)
    {
        var slot = SlotOf(cmd.Port);
        if (slot is null) return;
        var text = (cmd.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        if (text.Length > Dials.ChatMaxLen) text = text[..Dials.ChatMaxLen];

        // se codifica una vez aunque el reparto sea selectivo
        var frame = codec.Encode(new ChatBroadcast(cmd.Channel, slot.Data.PilotName, "",
            text, (ulong)clock.UnixMs));
        // GLOBAL a todos; FACTION solo a los de la misma faccion (CLAN llega en E5)
        foreach (var other in _players.Values)
        {
            if (cmd.Channel == ChatChannel.Faction && other.Data.Faction != slot.Data.Faction)
                continue;
            other.Port.Send(frame);
        }
    }

    // ─── salto de sector ────────────────────────────────────────────────────

    /// <summary>Lo levanta el Universo: (mundo de origen, cuenta, portal usado).</summary>
    internal event Action<World, long, PortalInfo>? Jump;

    /// <summary>Salto de sector. El mundo VALIDA; mover al jugador es del Universo,
    /// que es el unico que conoce los dos mapas.
    ///
    /// El cliente pide el salto cuando ARRANCA el encendido del portal, no cuando
    /// termina: esos 2,1 s de animacion son el hueco donde cabe este viaje. Si el
    /// server dice que no, la animacion se queda a medias y el error explica por
    /// que — nunca silencio.</summary>
    private void OnJump(JumpCmd cmd)
    {
        var slot = SlotOf(cmd.Port);
        if (slot is null) return;

        var portal = portals.FirstOrDefault(p => (ulong)p.Id == cmd.PortalId);
        if (portal is null)
        {
            Send(slot, new Failed(cmd.RequestId, ErrorCode.Gone, "ese portal no existe en este mapa"));
            return;
        }
        if (!portal.IsWorking)
        {
            Send(slot, new Failed(cmd.RequestId, ErrorCode.Invalid, "ese portal esta inactivo"));
            return;
        }
        if (slot.Dead)
        {
            Send(slot, new Failed(cmd.RequestId, ErrorCode.Invalid, "no se salta estando destruido"));
            return;
        }
        // el rango se valida en el SERVER aunque el cliente ya lo compruebe: el
        // cliente propone, el server dispone (y el cliente puede mentir)
        if (Geometry.Distance(slot.Entity.X, slot.Entity.Y, portal.X, portal.Y) > Dials.JumpRange)
        {
            Send(slot, new Failed(cmd.RequestId, ErrorCode.TooFar, "hay que estar junto al portal"));
            return;
        }
        Jump?.Invoke(this, slot.Data.AccountId, portal);
    }

    /// <summary>Le dice al cliente a donde reconectar. Sale ANTES de soltarlo:
    /// si se soltara primero, el socket ya estaria cerrado y el aviso no llegaria.</summary>
    internal void NotifyHandoff(long accountId, string mapCode, MapServer server)
    {
        if (!_players.TryGetValue(accountId, out var slot)) return;
        Send(slot, new JumpHandedOff(mapCode, server));
    }

    /// <summary>Suelta al jugador porque se va a OTRO servidor.
    ///
    /// Persiste su nave YA EN EL MAPA DESTINO y en el punto de llegada, y cierra
    /// el socket. Eso es todo lo que hace falta: cuando reconecte —aqui mismo o
    /// en otra maquina— el servidor que le toque leera de BD que esta en ese mapa
    /// y lo pondra ahi. El estado no viaja en manos del cliente ni por un canal
    /// nuevo entre servidores; viaja por donde ya viajaba.</summary>
    internal void ReleaseForJump(long accountId, long targetMapId, uint x, uint y)
    {
        if (!_players.TryGetValue(accountId, out var slot)) return;
        _players.Remove(accountId);
        Despawn(slot.Entity.Id, DespawnReason.Left);
        var (hp, esc) = (slot.Entity.Hp, slot.Entity.Shield);
        players.SaveShipState(accountId, targetMapId, x, y, hp, esc);
        // El socket NO se cierra aqui. Cerrarlo justo despues de mandar el aviso
        // era una carrera que el aviso perdia: el frame se queda en la cola de
        // salida y el cierre lo tira. Cierra el CLIENTE, que es quien sabe que ya
        // lo recibio. Si decide ignorarlo se queda con un socket sin jugador, y
        // de eso ya se encarga el ping.
    }
}
