// Entrar, volver, salir, moverse, hablar y saltar de sector.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    private void OnJoin(JoinCmd join)
    {
        // sesion unica: la conexion nueva expulsa a la vieja, avisando (nunca silencio)
        if (_players.TryGetValue(join.Player.AccountId, out var previo))
        {
            Enviar(previo, new SessionTakenOver());
            previo.Port.CloseSocket();
            Despawn(previo.Entity.Id, DespawnReason.Left);
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
        hero.Detener();

        var slot = new PlayerSlot
        {
            Port = join.Port, Entity = hero, Data = join.Player,
            SessionId = join.SessionId, LastPingTick = _tick,
            LaserDamage = join.LaserDamage, Cargo = join.Cargo,
            Credits = join.Player.Credits,
        };

        _players[join.Player.AccountId] = slot;
        SincronizarMundo(slot);
        Broadcast(new EntitySpawned(hero));          // los demas ven llegar al heroe
        log.LogInformation("cuenta {id} ({nombre}) entro al mapa {code}",
            join.Player.AccountId, join.Player.PilotName, map.Code);
    }

    /// <summary>Estado completo del mundo para un jugador: al entrar y al reconectar.</summary>
    private void SincronizarMundo(PlayerSlot slot)
    {
        // los portales van completos aqui: son mobiliario del mapa, no entidades
        Enviar(slot, new MapEntered(map, portals, CargoRiskPct: 100));
        Enviar(slot, new PricesPublished(npcPrices));
        Enviar(slot, new EntitySpawned(slot.Entity));
        Enviar(slot, HeroStatsDe(slot));
        foreach (var otro in _players.Values)
            if (otro != slot)
                Enviar(slot, new EntitySpawned(otro.Entity));
        foreach (var npc in _npcs.Values) Enviar(slot, new EntitySpawned(npc));
        foreach (var caja in _boxes.Values)
            Enviar(slot, new BoxSpawned(caja.Id, "from_ship", caja.X, caja.Y));
        EnviarAlmacen(slot);
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
                Enviar(cmd.Port, new Failed(0, ErrorCode.ResumeExpired));
                cmd.Port.CloseSocket();
                return;
            }
            Enviar(cmd.Port, new ResumeAccepted());
            OnJoin(new JoinCmd(cmd.Port, cmd.Player, cmd.SessionId, cmd.LaserDamage,
                cmd.MaxShield, cmd.Cargo ?? []));
            return;
        }
        slot.Port = cmd.Port;                    // el socket nuevo toma el relevo
        slot.GraceUntilTick = long.MaxValue;
        slot.PingMisses = 0;
        slot.LastPingTick = _tick;

        Enviar(slot, new ResumeAccepted());
        // re-sincronizacion completa: estado del mundo tal como esta ahora
        SincronizarMundo(slot);
        log.LogInformation("cuenta {id} reconecto dentro de la gracia", cmd.AccountId);
    }

    private void OnLeave(LeaveCmd leave)
    {
        var slot = SlotDe(leave.Port);
        if (slot is null) return;
        // LOGOUT explicito = se va de verdad; una caida de socket abre la ventana
        // de gracia y la nave se queda en el mundo (auth-v1)
        if (leave.Reason == "LOGOUT")
        {
            Drop(slot, leave.Reason);
            return;
        }
        if (slot.Desconectado) return;
        slot.GraceUntilTick = _tick + EnTicks(Diales.GraceMs);
        slot.LaserOn = false;
        log.LogInformation("cuenta {id}: socket caido, {s} s de gracia para reconectar",
            slot.Data.AccountId, Diales.GraceMs / 1000);
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
        var slot = SlotDe(move.Port);
        if (slot is null) return;
        if (slot.Muerto) return;
        // seq monotona: lo viejo o duplicado se descarta sin drama
        if (move.Seq <= slot.LastSeq) return;
        slot.LastSeq = move.Seq;
        // clamp server-side a los limites del mapa: el Moving eterno del legado, imposible
        slot.Entity.TargetX = Math.Clamp(move.TargetX, 0, map.BoundsX);
        slot.Entity.TargetY = Math.Clamp(move.TargetY, 0, map.BoundsY);
        // eco autoritativo a TODOS, heroe incluido: contra esto se reconcilia el cliente
        Broadcast(new EntityMoved(slot.Entity));
    }

    private void OnPong(PongCmd pong)
    {
        var slot = SlotDe(pong.Port);
        if (slot is null || pong.Nonce != slot.PingNonce) return;
        slot.PingMisses = 0;
    }

    // ─── chat ───────────────────────────────────────────────────────────────

    private void OnChatSend(ChatSendCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null) return;
        var texto = (cmd.Text ?? string.Empty).Trim();
        if (texto.Length == 0) return;
        if (texto.Length > Diales.ChatMaxLen) texto = texto[..Diales.ChatMaxLen];

        // se codifica una vez aunque el reparto sea selectivo
        var frame = codec.Encode(new ChatBroadcast(cmd.Channel, slot.Data.PilotName, "",
            texto, (ulong)clock.UnixMs));
        // GLOBAL a todos; FACTION solo a los de la misma faccion (CLAN llega en E5)
        foreach (var otro in _players.Values)
        {
            if (cmd.Channel == ChatChannel.Faction && otro.Data.Faction != slot.Data.Faction)
                continue;
            otro.Port.Send(frame);
        }
    }

    // ─── salto de sector ────────────────────────────────────────────────────

    /// <summary>Lo levanta el Universo: (mundo de origen, cuenta, portal usado).</summary>
    internal event Action<World, long, PortalInfo>? Saltar;

    /// <summary>Salto de sector. El mundo VALIDA; mover al jugador es del Universo,
    /// que es el unico que conoce los dos mapas.
    ///
    /// El cliente pide el salto cuando ARRANCA el encendido del portal, no cuando
    /// termina: esos 2,1 s de animacion son el hueco donde cabe este viaje. Si el
    /// server dice que no, la animacion se queda a medias y el error explica por
    /// que — nunca silencio.</summary>
    private void OnJump(JumpCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null) return;

        var portal = portals.FirstOrDefault(p => (ulong)p.Id == cmd.PortalId);
        if (portal is null)
        {
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.Gone, "ese portal no existe en este mapa"));
            return;
        }
        if (!portal.IsWorking)
        {
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.Invalid, "ese portal esta inactivo"));
            return;
        }
        if (slot.Muerto)
        {
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.Invalid, "no se salta estando destruido"));
            return;
        }
        // el rango se valida en el SERVER aunque el cliente ya lo compruebe: el
        // cliente propone, el server dispone (y el cliente puede mentir)
        if (Geometria.Distancia(slot.Entity.X, slot.Entity.Y, portal.X, portal.Y) > Diales.JumpRange)
        {
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.TooFar, "hay que estar junto al portal"));
            return;
        }
        Saltar?.Invoke(this, slot.Data.AccountId, portal);
    }

    /// <summary>Le dice al cliente a donde reconectar. Sale ANTES de soltarlo:
    /// si se soltara primero, el socket ya estaria cerrado y el aviso no llegaria.</summary>
    internal void AvisarHandoff(long accountId, string mapCode, MapServer servidor)
    {
        if (!_players.TryGetValue(accountId, out var slot)) return;
        Enviar(slot, new JumpHandedOff(mapCode, servidor));
    }

    /// <summary>Suelta al jugador porque se va a OTRO servidor.
    ///
    /// Persiste su nave YA EN EL MAPA DESTINO y en el punto de llegada, y cierra
    /// el socket. Eso es todo lo que hace falta: cuando reconecte —aqui mismo o
    /// en otra maquina— el servidor que le toque leera de BD que esta en ese mapa
    /// y lo pondra ahi. El estado no viaja en manos del cliente ni por un canal
    /// nuevo entre servidores; viaja por donde ya viajaba.</summary>
    internal void SoltarPorSalto(long accountId, long mapaDestino, uint x, uint y)
    {
        if (!_players.TryGetValue(accountId, out var slot)) return;
        _players.Remove(accountId);
        Despawn(slot.Entity.Id, DespawnReason.Left);
        var (hp, esc) = (slot.Entity.Hp, slot.Entity.Shield);
        players.SaveShipState(accountId, mapaDestino, x, y, hp, esc);
        // El socket NO se cierra aqui. Cerrarlo justo despues de mandar el aviso
        // era una carrera que el aviso perdia: el frame se queda en la cola de
        // salida y el cierre lo tira. Cierra el CLIENTE, que es quien sabe que ya
        // lo recibio. Si decide ignorarlo se queda con un socket sin jugador, y
        // de eso ya se encarga el ping.
    }
}
