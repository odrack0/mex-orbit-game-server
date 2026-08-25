// El mundo: UN hilo de simulacion con tick fijo. Todo estado vive aqui; las
// conexiones solo postean comandos al inbox y reciben mensajes ya codificados.
// La leccion del TickManager legado: ninguna excepcion de un tick puede matar
// el loop — cada tick esta blindado y el error se loguea con contexto.
using System.Threading.Channels;
using MexOrbit.GameServer.Data;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Game;

public interface IClientPort
{
    long AccountId { get; }
    void Send(byte[] frame);
    void CloseSocket();
}

public abstract record WorldCmd;
public sealed record JoinCmd(IClientPort Port, PlayerData Player, long SessionId) : WorldCmd;
public sealed record LeaveCmd(IClientPort Port, string Reason) : WorldCmd;
public sealed record MoveIntentCmd(IClientPort Port, MoveIntent Intent) : WorldCmd;
public sealed record PongCmd(IClientPort Port, ulong Nonce) : WorldCmd;

public sealed class World(MapInfo map, List<NpcSpawnInfo> npcSpawns, Repo repo, ILogger<World> log,
    int tickMs, int pingIntervalSeconds, int pingMissesToDrop)
{
    private readonly Channel<WorldCmd> _inbox = Channel.CreateUnbounded<WorldCmd>();

    private sealed class PlayerSlot
    {
        public required IClientPort Port;
        public required Entity Entity;
        public required PlayerData Data;
        public required long SessionId;
        public ulong LastSeq;
        public ulong PingNonce;
        public int PingMisses;
        public long LastPingTick;
    }

    private readonly Dictionary<long, PlayerSlot> _players = new();     // account_id -> slot
    private readonly Dictionary<ulong, Entity> _npcs = new();
    private readonly Random _rng = new(20260825);
    private long _tick;

    public void Post(WorldCmd cmd) => _inbox.Writer.TryWrite(cmd);

    public void SpawnNpcs()
    {
        ulong nextId = 1_000_000;
        foreach (var spawn in npcSpawns)
            for (var i = 0; i < spawn.Amount; i++)
            {
                var e = new Entity
                {
                    Id = nextId++,
                    Kind = EntityKind.Npc,
                    TypeId = spawn.Code,
                    Name = spawn.DisplayName,
                    Speed = spawn.Speed,
                    Hp = spawn.MaxHp,
                    MaxHp = spawn.MaxHp,
                    X = _rng.Next(500, (int)map.BoundsX - 500),
                    Y = _rng.Next(500, (int)map.BoundsY - 500),
                };
                e.TargetX = e.X;
                e.TargetY = e.Y;
                _npcs[e.Id] = e;
            }
        log.LogInformation("Mapa {code}: {n} NPCs poblados", map.Code, _npcs.Count);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var dt = tickMs / 1000.0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            _tick++;
            try { Tick(dt); }
            catch (Exception ex)
            {
                // el tick JAMAS tumba el loop: se loguea y se sigue (leccion del legado)
                log.LogError(ex, "excepcion en tick {tick}", _tick);
            }
        }
    }

    private void Tick(double dt)
    {
        while (_inbox.Reader.TryRead(out var cmd)) Handle(cmd);

        // NPCs: deambular perezoso dentro del mapa
        foreach (var npc in _npcs.Values)
        {
            if (!npc.Moving && _rng.NextDouble() < 0.004)
            {
                npc.TargetX = Math.Clamp(npc.X + _rng.Next(-800, 801), 0, map.BoundsX);
                npc.TargetY = Math.Clamp(npc.Y + _rng.Next(-800, 801), 0, map.BoundsY);
                Broadcast(npc.ToMove().Encode());
            }
            npc.Step(dt);
        }
        foreach (var slot in _players.Values) slot.Entity.Step(dt);

        // heartbeat: ping con nonce; N sin respuesta = socket muerto
        var pingCadaTicks = pingIntervalSeconds * 1000 / tickMs;
        foreach (var slot in _players.Values.ToList())
        {
            if (_tick - slot.LastPingTick < pingCadaTicks) continue;
            if (slot.PingMisses >= pingMissesToDrop)
            {
                log.LogInformation("cuenta {id}: {n} pings sin respuesta, cerrando", slot.Port.AccountId, slot.PingMisses);
                Drop(slot, "TIMEOUT");
                continue;
            }
            slot.PingNonce = (ulong)_rng.NextInt64(1, long.MaxValue);
            slot.PingMisses++;
            slot.LastPingTick = _tick;
            slot.Port.Send(new Ping { Nonce = slot.PingNonce }.Encode());
        }

        // write-behind del estado (cada ~30 s por jugador, fuera del hilo del tick)
        if (_tick % (30_000 / tickMs) == 0)
            foreach (var slot in _players.Values)
            {
                var (id, mapId, x, y, hp) = (slot.Data.AccountId, map.Id,
                    (uint)slot.Entity.X, (uint)slot.Entity.Y, slot.Entity.Hp);
                _ = Task.Run(() => Safe(() => repo.SaveShipState(id, mapId, x, y, hp), "SaveShipState"));
                var sid = slot.SessionId;
                _ = Task.Run(() => Safe(() => repo.TouchSession(sid), "TouchSession"));
            }
    }

    private void Handle(WorldCmd cmd)
    {
        switch (cmd)
        {
            case JoinCmd join: OnJoin(join); break;
            case LeaveCmd leave: OnLeave(leave); break;
            case MoveIntentCmd move: OnMoveIntent(move); break;
            case PongCmd pong: OnPong(pong); break;
        }
    }

    private void OnJoin(JoinCmd join)
    {
        // sesion unica: la conexion nueva expulsa a la vieja, avisando (nunca silencio)
        if (_players.TryGetValue(join.Player.AccountId, out var previo))
        {
            previo.Port.Send(new SessionReplaced().Encode());
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
            X = join.Player.PosX,
            Y = join.Player.PosY,
        };
        hero.TargetX = hero.X;
        hero.TargetY = hero.Y;

        var slot = new PlayerSlot
        {
            Port = join.Port, Entity = hero, Data = join.Player,
            SessionId = join.SessionId, LastPingTick = _tick,
        };

        // sincronizacion inicial: mapa -> heroe -> stats -> el resto del mundo
        slot.Port.Send(new EnterMap
        {
            MapId = (ulong)map.Id, MapCode = map.Code,
            LimitsX = map.BoundsX, LimitsY = map.BoundsY, CargoRiskPct = 100,
        }.Encode());
        slot.Port.Send(hero.ToSpawn().Encode());
        slot.Port.Send(new HeroStats
        {
            Hp = hero.Hp, MaxHp = hero.MaxHp, Shield = 0, MaxShield = 0,
            Cargo = 0, MaxCargo = join.Player.CargoCapacity,
            Credits = (ulong)join.Player.Credits, Experience = 0, Level = 1,
        }.Encode());
        foreach (var otro in _players.Values) slot.Port.Send(otro.Entity.ToSpawn().Encode());
        foreach (var npc in _npcs.Values) slot.Port.Send(npc.ToSpawn().Encode());

        Broadcast(hero.ToSpawn().Encode());          // los demas ven llegar al heroe
        _players[join.Player.AccountId] = slot;
        log.LogInformation("cuenta {id} ({nombre}) entro al mapa {code}",
            join.Player.AccountId, join.Player.PilotName, map.Code);
    }

    private void OnLeave(LeaveCmd leave)
    {
        var slot = _players.Values.FirstOrDefault(s => ReferenceEquals(s.Port, leave.Port));
        if (slot is null) return;
        Drop(slot, leave.Reason);
    }

    private void Drop(PlayerSlot slot, string reason)
    {
        _players.Remove(slot.Data.AccountId);
        Despawn(slot.Entity.Id, DespawnReason.Left);
        slot.Port.CloseSocket();
        var (id, mapId, x, y, hp, sid) = (slot.Data.AccountId, map.Id,
            (uint)slot.Entity.X, (uint)slot.Entity.Y, slot.Entity.Hp, slot.SessionId);
        _ = Task.Run(() => Safe(() =>
        {
            repo.SaveShipState(id, mapId, x, y, hp);   // el estado siempre se persiste al salir
            repo.CloseSession(sid, reason);
        }, "Drop"));
        log.LogInformation("cuenta {id} salio ({reason})", id, reason);
    }

    private void OnMoveIntent(MoveIntentCmd move)
    {
        var slot = _players.Values.FirstOrDefault(s => ReferenceEquals(s.Port, move.Port));
        if (slot is null) return;
        // seq monotona: lo viejo o duplicado se descarta sin drama
        if (move.Intent.Seq <= slot.LastSeq) return;
        slot.LastSeq = move.Intent.Seq;
        // clamp server-side a los limites del mapa: el Moving eterno del legado, imposible
        slot.Entity.TargetX = Math.Clamp(move.Intent.TargetX, 0, map.BoundsX);
        slot.Entity.TargetY = Math.Clamp(move.Intent.TargetY, 0, map.BoundsY);
        // eco autoritativo a TODOS, heroe incluido: contra esto se reconcilia el cliente
        Broadcast(slot.Entity.ToMove().Encode());
    }

    private void OnPong(PongCmd pong)
    {
        var slot = _players.Values.FirstOrDefault(s => ReferenceEquals(s.Port, pong.Port));
        if (slot is null || pong.Nonce != slot.PingNonce) return;
        slot.PingMisses = 0;
    }

    private void Despawn(ulong entityId, DespawnReason reason) =>
        Broadcast(new EntityDespawn { EntityId = entityId, Reason = reason }.Encode());

    private void Broadcast(byte[] frame)
    {
        foreach (var slot in _players.Values) slot.Port.Send(frame);
    }

    private void Safe(Action accion, string que)
    {
        try { accion(); }
        catch (Exception ex) { log.LogError(ex, "fallo en {que}", que); }
    }
}
