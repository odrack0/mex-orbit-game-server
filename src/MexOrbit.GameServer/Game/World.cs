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
public sealed record JoinCmd(IClientPort Port, PlayerData Player, long SessionId, uint LaserDamage,
    Dictionary<long, uint> Cargo) : WorldCmd;
public sealed record LeaveCmd(IClientPort Port, string Reason) : WorldCmd;
public sealed record MoveIntentCmd(IClientPort Port, MoveIntent Intent) : WorldCmd;
public sealed record PongCmd(IClientPort Port, ulong Nonce) : WorldCmd;
public sealed record SelectTargetCmd(IClientPort Port, ulong EntityId) : WorldCmd;
public sealed record LaserToggleCmd(IClientPort Port, bool Active) : WorldCmd;
public sealed record CollectBoxCmd(IClientPort Port, ulong RequestId, ulong BoxId) : WorldCmd;

public sealed class World(MapInfo map, List<NpcSpawnInfo> npcSpawns, List<MaterialBias> zoneBias,
    Repo repo, ILogger<World> log, int tickMs, int pingIntervalSeconds, int pingMissesToDrop)
{
    // Diales de combate y loot del slice (documentados en el README del repo).
    // Los numeros de JUEGO (recompensas, drops) viven en BD; esto es cadencia/alcance.
    private const double LaserRange = 600;
    private const int AttackIntervalMs = 500;
    private const double CollectRange = 250;
    private const int BoxTtlMs = 150_000;      // §7 guidelines: despawn de caja 2-3 min

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
        // combate y carga
        public ulong TargetId;
        public bool LaserOn;
        public required uint LaserDamage;
        public required Dictionary<long, uint> Cargo;   // server_item_id -> unidades
        public long NextAttackTick;
        public decimal Credits;
        public uint CargoUsed => (uint)Cargo.Values.Sum(v => (long)v);
    }

    private sealed class BoxState
    {
        public required ulong Id;
        public required double X;
        public required double Y;
        public required Dictionary<long, uint> Drops;   // server_item_id -> unidades
        public required long ExpiraTick;
    }

    private readonly Dictionary<long, PlayerSlot> _players = new();     // account_id -> slot
    private readonly Dictionary<ulong, Entity> _npcs = new();
    private readonly Dictionary<ulong, NpcSpawnInfo> _npcInfo = new();  // entity_id -> catalogo
    private readonly Dictionary<ulong, BoxState> _boxes = new();
    private readonly List<(long Tick, NpcSpawnInfo Info, ulong Id)> _respawns = new();
    private readonly Dictionary<long, string> _lootIds = zoneBias.ToDictionary(b => b.ItemId, b => b.LootId);
    private ulong _nextBoxId = 5_000_000;
    private readonly Random _rng = new(20260825);
    private long _tick;

    public void Post(WorldCmd cmd) => _inbox.Writer.TryWrite(cmd);

    public void SpawnNpcs()
    {
        ulong nextId = 1_000_000;
        foreach (var spawn in npcSpawns)
            for (var i = 0; i < spawn.Amount; i++)
                SpawnNpc(spawn, nextId++);
        log.LogInformation("Mapa {code}: {n} NPCs poblados", map.Code, _npcs.Count);
    }

    private Entity SpawnNpc(NpcSpawnInfo spawn, ulong id)
    {
        var e = new Entity
        {
            Id = id,
            Kind = EntityKind.Npc,
            TypeId = spawn.Code,
            Name = spawn.DisplayName,
            Speed = spawn.Speed,
            Hp = spawn.MaxHp,
            MaxHp = spawn.MaxHp,
            Shield = spawn.MaxShield,
            MaxShield = spawn.MaxShield,
            X = _rng.Next(500, (int)map.BoundsX - 500),
            Y = _rng.Next(500, (int)map.BoundsY - 500),
        };
        e.TargetX = e.X;
        e.TargetY = e.Y;
        _npcs[e.Id] = e;
        _npcInfo[e.Id] = spawn;
        return e;
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

        // combate: laser encendido + objetivo vivo + en rango = un golpe por intervalo
        foreach (var slot in _players.Values)
        {
            if (!slot.LaserOn || _tick < slot.NextAttackTick) continue;
            if (!_npcs.TryGetValue(slot.TargetId, out var npc)) { slot.LaserOn = false; continue; }
            var dist = Math.Sqrt(Math.Pow(npc.X - slot.Entity.X, 2) + Math.Pow(npc.Y - slot.Entity.Y, 2));
            if (dist > LaserRange) continue;    // fuera de rango: el laser espera, no se apaga
            slot.NextAttackTick = _tick + AttackIntervalMs / tickMs;
            AplicarDanio(slot, npc);
        }

        // cajas: expiran a los 2-3 min (§7)
        if (_tick % (1000 / tickMs) == 0)
            foreach (var caja in _boxes.Values.Where(b => _tick >= b.ExpiraTick).ToList())
            {
                _boxes.Remove(caja.Id);
                Broadcast(new BoxDespawn { BoxId = caja.Id, Reason = BoxDespawnReason.Expired }.Encode());
            }

        // respawns de NPC
        foreach (var (cuando, info, id) in _respawns.Where(r => _tick >= r.Tick).ToList())
        {
            _respawns.Remove((cuando, info, id));
            var e = SpawnNpc(info, id);
            Broadcast(e.ToSpawn().Encode());
        }

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
            case SelectTargetCmd sel: OnSelectTarget(sel); break;
            case LaserToggleCmd laser: OnLaserToggle(laser); break;
            case CollectBoxCmd collect: OnCollectBox(collect); break;
        }
    }

    // ─── combate ────────────────────────────────────────────────────────────

    private void OnSelectTarget(SelectTargetCmd sel)
    {
        var slot = SlotDe(sel.Port);
        if (slot is null) return;
        if (sel.EntityId == 0 || !_npcs.TryGetValue(sel.EntityId, out var npc))
        {
            slot.TargetId = 0;
            slot.LaserOn = false;
            return;
        }
        slot.TargetId = sel.EntityId;
        slot.Port.Send(new TargetInfo
        {
            EntityId = npc.Id, Hp = npc.Hp, MaxHp = npc.MaxHp,
            Shield = npc.Shield, MaxShield = npc.MaxShield,
        }.Encode());
    }

    private void OnLaserToggle(LaserToggleCmd laser)
    {
        var slot = SlotDe(laser.Port);
        if (slot is null) return;
        slot.LaserOn = laser.Active && slot.TargetId != 0;
    }

    private void AplicarDanio(PlayerSlot slot, Entity npc)
    {
        // el escudo absorbe primero; los valores del evento son POST-daño, siempre
        var danio = slot.LaserDamage;
        var alEscudo = Math.Min(npc.Shield, danio);
        npc.Shield -= alEscudo;
        var alCasco = Math.Min(npc.Hp, danio - alEscudo);
        npc.Hp -= alCasco;
        Broadcast(new AttackEvent
        {
            AttackerId = slot.Entity.Id, TargetId = npc.Id, Weapon = Weapon.Laser,
            Damage = danio, TargetHp = npc.Hp, TargetShield = npc.Shield, Missed = false,
        }.Encode());
        if (npc.Hp == 0) OnNpcMuerto(slot, npc);
    }

    private void OnNpcMuerto(PlayerSlot slot, Entity npc)
    {
        var info = _npcInfo[npc.Id];
        _npcs.Remove(npc.Id);
        _npcInfo.Remove(npc.Id);
        foreach (var s in _players.Values.Where(s => s.TargetId == npc.Id))
        {
            s.TargetId = 0;
            s.LaserOn = false;
        }
        Broadcast(new EntityDestroyed { EntityId = npc.Id, KillerId = slot.Entity.Id }.Encode());
        _respawns.Add((_tick + info.RespawnSeconds * 1000 / tickMs, info, npc.Id));

        // recompensa: credits relativos + ledger (la api jamas toca esto en sesion)
        var credits = (decimal)info.RewardCredits;
        slot.Credits += credits;
        var accountId = slot.Data.AccountId;
        _ = Task.Run(() => Safe(() => repo.AddCredits(accountId, credits, "NPC_KILL", (long)npc.Id), "AddCredits"));
        slot.Port.Send(HeroStatsDe(slot).Encode());

        // la caja: el NPC pone la cantidad, la ZONA pone la mezcla (§4 guidelines)
        var total = (uint)_rng.Next((int)info.CargoDropMin, (int)info.CargoDropMax + 1);
        var pesoTotal = zoneBias.Sum(b => b.Weight);
        var drops = new Dictionary<long, uint>();
        uint repartido = 0;
        foreach (var bias in zoneBias)
        {
            var unidades = (uint)Math.Round(total * bias.Weight / pesoTotal);
            if (unidades > 0) { drops[bias.ItemId] = unidades; repartido += unidades; }
        }
        if (repartido == 0) return;
        var caja = new BoxState
        {
            Id = _nextBoxId++, X = npc.X, Y = npc.Y, Drops = drops,
            ExpiraTick = _tick + BoxTtlMs / tickMs,
        };
        _boxes[caja.Id] = caja;
        Broadcast(new BoxSpawn
        {
            BoxId = caja.Id, BoxType = "from_ship",
            X = (ulong)Math.Round(caja.X), Y = (ulong)Math.Round(caja.Y),
        }.Encode());
    }

    // ─── recoleccion ────────────────────────────────────────────────────────

    private void OnCollectBox(CollectBoxCmd collect)
    {
        var slot = SlotDe(collect.Port);
        if (slot is null) return;
        if (!_boxes.TryGetValue(collect.BoxId, out var caja))
        {
            slot.Port.Send(new ErrorReply { RequestId = collect.RequestId, Code = ErrorCode.Gone }.Encode());
            return;
        }
        // la validacion que el legado dejaba al cliente, donde debe estar: aqui
        var dist = Math.Sqrt(Math.Pow(caja.X - slot.Entity.X, 2) + Math.Pow(caja.Y - slot.Entity.Y, 2));
        if (dist > CollectRange)
        {
            slot.Port.Send(new ErrorReply { RequestId = collect.RequestId, Code = ErrorCode.TooFar }.Encode());
            return;
        }
        var espacio = slot.Data.CargoCapacity - slot.CargoUsed;
        if (espacio == 0)
        {
            slot.Port.Send(new ErrorReply
            {
                RequestId = collect.RequestId, Code = ErrorCode.Insufficient, Detail = "bodega llena",
            }.Encode());
            return;
        }

        // toma lo que quepa; el resto queda en la caja hasta su expiracion
        var tomados = new List<(long ItemId, uint Amount)>();
        foreach (var (itemId, disponible) in caja.Drops.ToList())
        {
            if (espacio == 0) break;
            var toma = Math.Min(disponible, espacio);
            tomados.Add((itemId, toma));
            espacio -= toma;
            if (toma == disponible) caja.Drops.Remove(itemId);
            else caja.Drops[itemId] = disponible - toma;
        }

        try
        {
            // sincrono a proposito: el CollectResult solo sale si la BD ya lo tiene
            repo.AddCargoPickup(slot.Data.AccountId, tomados, (long)caja.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo AddCargoPickup cuenta {id}", slot.Data.AccountId);
            slot.Port.Send(new ErrorReply { RequestId = collect.RequestId, Code = ErrorCode.Generic }.Encode());
            return;
        }
        foreach (var (itemId, amount) in tomados)
            slot.Cargo[itemId] = slot.Cargo.GetValueOrDefault(itemId) + amount;

        var resultado = new CollectResult { RequestId = collect.RequestId };
        foreach (var (itemId, amount) in tomados)
            resultado.Drops.Add(new MaterialAmount { MaterialId = _lootIds[itemId], Amount = amount });
        slot.Port.Send(resultado.Encode());
        slot.Port.Send(HeroStatsDe(slot).Encode());

        if (caja.Drops.Count == 0)
        {
            _boxes.Remove(caja.Id);
            Broadcast(new BoxDespawn { BoxId = caja.Id, Reason = BoxDespawnReason.Collected }.Encode());
        }
    }

    private PlayerSlot? SlotDe(IClientPort port) =>
        _players.Values.FirstOrDefault(s => ReferenceEquals(s.Port, port));

    private HeroStats HeroStatsDe(PlayerSlot slot) => new()
    {
        Hp = slot.Entity.Hp, MaxHp = slot.Entity.MaxHp,
        Shield = slot.Entity.Shield, MaxShield = slot.Entity.MaxShield,
        Cargo = slot.CargoUsed, MaxCargo = slot.Data.CargoCapacity,
        Credits = (ulong)slot.Credits, Experience = 0, Level = 1,
    };

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
            LaserDamage = join.LaserDamage, Cargo = join.Cargo,
            Credits = join.Player.Credits,
        };

        // sincronizacion inicial: mapa -> heroe -> stats -> el resto del mundo
        slot.Port.Send(new EnterMap
        {
            MapId = (ulong)map.Id, MapCode = map.Code,
            LimitsX = map.BoundsX, LimitsY = map.BoundsY, CargoRiskPct = 100,
        }.Encode());
        slot.Port.Send(hero.ToSpawn().Encode());
        slot.Port.Send(HeroStatsDe(slot).Encode());
        foreach (var otro in _players.Values) slot.Port.Send(otro.Entity.ToSpawn().Encode());
        foreach (var npc in _npcs.Values) slot.Port.Send(npc.ToSpawn().Encode());
        foreach (var caja in _boxes.Values)
            slot.Port.Send(new BoxSpawn
            {
                BoxId = caja.Id, BoxType = "from_ship",
                X = (ulong)Math.Round(caja.X), Y = (ulong)Math.Round(caja.Y),
            }.Encode());

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
