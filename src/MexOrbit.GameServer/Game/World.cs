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
    uint MaxShield, Dictionary<long, uint> Cargo) : WorldCmd;
public sealed record LeaveCmd(IClientPort Port, string Reason) : WorldCmd;
public sealed record MoveIntentCmd(IClientPort Port, MoveIntent Intent) : WorldCmd;
public sealed record PongCmd(IClientPort Port, ulong Nonce) : WorldCmd;
public sealed record SelectTargetCmd(IClientPort Port, ulong EntityId) : WorldCmd;
public sealed record LaserToggleCmd(IClientPort Port, bool Active) : WorldCmd;
public sealed record CollectBoxCmd(IClientPort Port, ulong RequestId, ulong BoxId) : WorldCmd;
public sealed record UnloadCargoCmd(IClientPort Port, ulong RequestId) : WorldCmd;
public sealed record SellToNpcCmd(IClientPort Port, ulong RequestId, string MaterialId, ulong Amount) : WorldCmd;
/// <summary>Reconexion dentro de la ventana de gracia: el puerto viejo se sustituye
/// por el nuevo y la nave sigue donde estaba, sin recrearla.</summary>
public sealed record ResumeCmd(IClientPort Port, long AccountId, long SessionId) : WorldCmd;
public sealed record ChatSendCmd(IClientPort Port, ulong RequestId, ChatChannel Channel, string Text) : WorldCmd;

public sealed class World(MapInfo map, List<NpcSpawnInfo> npcSpawns, List<MaterialBias> zoneBias,
    RefineRecipe? refineRecipe, List<NpcPrice> npcPrices, List<PortalInfo> portals,
    Repo repo, ILogger<World> log, int tickMs, int pingIntervalSeconds, int pingMissesToDrop)
{
    // Diales de combate y loot del slice (documentados en el README del repo).
    // Los numeros de JUEGO (recompensas, drops) viven en BD; esto es cadencia/alcance.
    private const double LaserRange = 600;
    private const int AttackIntervalMs = 500;
    private const double CollectRange = 250;
    private const int BoxTtlMs = 150_000;      // §7 guidelines: despawn de caja 2-3 min
    private const int GraceMs = 60_000;        // ventana de reconexion (auth-v1)
    private const int ChatMaxLen = 256;

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
        public bool EnBase;                              // dentro del rango de la estacion
        public string AmmoId = "ammo_cel_1";             // municion equipada (E3 la hara elegible)
        public bool Skilled;                             // disparo potenciado (perfil de piloto, E4)
        /// <summary>Tick en que expira la gracia; long.MaxValue = socket vivo.</summary>
        public long GraceUntilTick = long.MaxValue;
        public bool Desconectado => GraceUntilTick != long.MaxValue;
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
    private readonly Dictionary<long, string> _lootIds =
        zoneBias.ToDictionary(b => b.ItemId, b => b.LootId)
            .Concat(npcPrices.ToDictionary(p => p.ItemId, p => p.LootId))
            .Concat(refineRecipe is null
                ? new Dictionary<long, string>()
                : new Dictionary<long, string> { [refineRecipe.OutputItemId] = refineRecipe.OutputLootId })
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
    private readonly Dictionary<string, NpcPrice> _preciosPorLoot = npcPrices.ToDictionary(p => p.LootId);
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
            // bajo fuego reciente el NPC no deambula: se queda a pelear (bueno
            // para el juego y para que el combate no se escape del rango)
            if (_tick - npc.LastHitTick < 3000 / tickMs) continue;
            if (!npc.Moving && _rng.NextDouble() < 0.004)
            {
                npc.TargetX = Math.Clamp(npc.X + _rng.Next(-800, 801), 0, map.BoundsX);
                npc.TargetY = Math.Clamp(npc.Y + _rng.Next(-800, 801), 0, map.BoundsY);
                Broadcast(npc.ToMove().Encode());
            }
            npc.Step(dt);
        }
        foreach (var slot in _players.Values)
        {
            slot.Entity.Step(dt);
            ActualizarRangoBase(slot);
        }

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

        // gracia agotada: la nave sale del mundo y la sesion se cierra
        foreach (var slot in _players.Values.Where(s => s.Desconectado && _tick >= s.GraceUntilTick).ToList())
        {
            log.LogInformation("cuenta {id}: gracia agotada, saliendo del mundo", slot.Data.AccountId);
            Drop(slot, "TIMEOUT");
        }

        // heartbeat: ping con nonce; N sin respuesta = socket muerto
        var pingCadaTicks = pingIntervalSeconds * 1000 / tickMs;
        foreach (var slot in _players.Values.ToList())
        {
            if (slot.Desconectado) continue;      // sin socket no hay a quien pingear
            if (_tick - slot.LastPingTick < pingCadaTicks) continue;
            if (slot.PingMisses >= pingMissesToDrop)
            {
                // socket mudo: se cierra y empieza la gracia (no se pierde la nave)
                log.LogInformation("cuenta {id}: {n} pings sin respuesta, abriendo gracia",
                    slot.Data.AccountId, slot.PingMisses);
                slot.Port.CloseSocket();
                slot.GraceUntilTick = _tick + GraceMs / tickMs;
                slot.LaserOn = false;
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
                var (id, mapId, x, y, hp, esc) = (slot.Data.AccountId, map.Id,
                    (uint)slot.Entity.X, (uint)slot.Entity.Y, slot.Entity.Hp, slot.Entity.Shield);
                _ = Task.Run(() => Safe(() => repo.SaveShipState(id, mapId, x, y, hp, esc), "SaveShipState"));
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
            case UnloadCargoCmd unload: OnUnloadCargo(unload); break;
            case SellToNpcCmd sell: OnSellToNpc(sell); break;
            case ResumeCmd resume: OnResume(resume); break;
            case ChatSendCmd chat: OnChatSend(chat); break;
        }
    }

    // ─── reconexion y chat ──────────────────────────────────────────────────

    /// <summary>El jugador vuelve dentro de la gracia: se le devuelve su nave donde
    /// quedo, sin recrearla ni tocar su carga (auth-v1: resume de sesion).</summary>
    private void OnResume(ResumeCmd cmd)
    {
        if (!_players.TryGetValue(cmd.AccountId, out var slot))
        {
            cmd.Port.Send(new ErrorReply { Code = ErrorCode.ResumeExpired }.Encode());
            cmd.Port.CloseSocket();
            return;
        }
        slot.Port = cmd.Port;                    // el socket nuevo toma el relevo
        slot.GraceUntilTick = long.MaxValue;
        slot.PingMisses = 0;
        slot.LastPingTick = _tick;

        cmd.Port.Send(new ResumeOk().Encode());
        // re-sincronizacion completa: estado del mundo tal como esta ahora
        SincronizarMundo(slot);
        log.LogInformation("cuenta {id} reconecto dentro de la gracia", cmd.AccountId);
    }

    private void OnChatSend(ChatSendCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null) return;
        var texto = (cmd.Text ?? string.Empty).Trim();
        if (texto.Length == 0) return;
        if (texto.Length > ChatMaxLen) texto = texto[..ChatMaxLen];

        var msg = new ChatMessage
        {
            Channel = cmd.Channel,
            FromName = slot.Data.PilotName,
            FromClan = "",
            Text = texto,
            ServerTimeMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }.Encode();
        // GLOBAL a todos; FACTION solo a los de la misma faccion (CLAN llega en E5)
        foreach (var otro in _players.Values)
        {
            if (cmd.Channel == ChatChannel.Faction && otro.Data.Faction != slot.Data.Faction)
                continue;
            otro.Port.Send(msg);
        }
    }

    // ─── la base ────────────────────────────────────────────────────────────

    /// <summary>Entrar o salir del rango de la estacion abre/cierra su panel.</summary>
    private void ActualizarRangoBase(PlayerSlot slot)
    {
        var dist = Math.Sqrt(Math.Pow(map.StationX - slot.Entity.X, 2)
                             + Math.Pow(map.StationY - slot.Entity.Y, 2));
        var dentro = dist <= map.SecureRange;
        if (dentro == slot.EnBase) return;
        slot.EnBase = dentro;
        slot.Port.Send(new StationRange { InRange = dentro, StationId = (ulong)map.Id }.Encode());
    }

    private void OnUnloadCargo(UnloadCargoCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null) return;
        if (!slot.EnBase)
        {
            slot.Port.Send(new ErrorReply
            {
                RequestId = cmd.RequestId, Code = ErrorCode.TooFar, Detail = "fuera de la base",
            }.Encode());
            return;
        }
        UnloadOutcome resultado;
        try
        {
            // sincrono: la respuesta solo sale si la BD ya lo tiene
            resultado = repo.UnloadAndRefine(slot.Data.AccountId, refineRecipe);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo UnloadAndRefine cuenta {id}", slot.Data.AccountId);
            slot.Port.Send(new ErrorReply { RequestId = cmd.RequestId, Code = ErrorCode.Generic }.Encode());
            return;
        }
        slot.Cargo.Clear();
        var msg = new UnloadResult { RequestId = cmd.RequestId };
        foreach (var (itemId, amount) in resultado.Stored)
            msg.Stored.Add(new MaterialAmount { MaterialId = _lootIds[itemId], Amount = amount });
        foreach (var (itemId, amount) in resultado.Refined)
            msg.Refined.Add(new MaterialAmount { MaterialId = _lootIds[itemId], Amount = amount });
        slot.Port.Send(msg.Encode());
        slot.Port.Send(HeroStatsDe(slot).Encode());
        EnviarAlmacen(slot);
    }

    private void OnSellToNpc(SellToNpcCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null) return;
        if (!slot.EnBase)
        {
            slot.Port.Send(new ErrorReply
            {
                RequestId = cmd.RequestId, Code = ErrorCode.TooFar, Detail = "fuera de la base",
            }.Encode());
            return;
        }
        if (!_preciosPorLoot.TryGetValue(cmd.MaterialId, out var precio))
        {
            slot.Port.Send(new ErrorReply
            {
                RequestId = cmd.RequestId, Code = ErrorCode.Invalid, Detail = "el NPC no compra eso",
            }.Encode());
            return;
        }
        (uint Sold, decimal Gained, decimal NewCredits) venta;
        try
        {
            venta = repo.SellToNpc(slot.Data.AccountId, precio.ItemId, (uint)cmd.Amount, precio.PriceCredits);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo SellToNpc cuenta {id}", slot.Data.AccountId);
            slot.Port.Send(new ErrorReply { RequestId = cmd.RequestId, Code = ErrorCode.Generic }.Encode());
            return;
        }
        if (venta.Sold == 0)
        {
            slot.Port.Send(new ErrorReply
            {
                RequestId = cmd.RequestId, Code = ErrorCode.Insufficient, Detail = "sin existencias",
            }.Encode());
            return;
        }
        slot.Credits = venta.NewCredits;
        slot.Port.Send(new SellResult
        {
            RequestId = cmd.RequestId,
            CreditsGained = (ulong)venta.Gained,
            NewCredits = (ulong)venta.NewCredits,
        }.Encode());
        slot.Port.Send(HeroStatsDe(slot).Encode());
        EnviarAlmacen(slot);
    }

    private void EnviarAlmacen(PlayerSlot slot)
    {
        var accountId = slot.Data.AccountId;
        var port = slot.Port;
        // lectura fuera del hilo del tick: el estado ya se persistio
        _ = Task.Run(() => Safe(() =>
        {
            var saldos = repo.LoadStorage(accountId);
            var msg = new StorageState();
            foreach (var (lootId, amount) in saldos)
                msg.Materials.Add(new MaterialAmount { MaterialId = lootId, Amount = (uint)amount });
            port.Send(msg.Encode());
        }, "EnviarAlmacen"));
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
        npc.LastHitTick = _tick;
        // el golpe lo frena en seco donde este (y avisa a todos)
        if (npc.Moving)
        {
            npc.TargetX = npc.X;
            npc.TargetY = npc.Y;
            Broadcast(npc.ToMove().Encode());
        }
        Broadcast(new AttackEvent
        {
            AttackerId = slot.Entity.Id, TargetId = npc.Id, Weapon = Weapon.Laser,
            Damage = danio, TargetHp = npc.Hp, TargetShield = npc.Shield, Missed = false,
            // el aspecto del disparo: la municion equipada y si va potenciada.
            // En el slice hay una sola municion y el perfil de piloto llega en E4,
            // asi que van fijos; el contrato ya los transporta.
            AmmoId = slot.AmmoId, Skilled = slot.Skilled,
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
            // el escudo del casco + sus generadores. En E2 se entra con el escudo
            // LLENO (salir de la base lo recarga): la regeneracion en vuelo aun no
            // existe, y arrastrar un 0 guardado dejaria al jugador sin escudo para
            // siempre. Se persiste igual, para cuando la regeneracion llegue.
            Shield = join.MaxShield,
            MaxShield = join.MaxShield,
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

        _players[join.Player.AccountId] = slot;
        SincronizarMundo(slot);
        Broadcast(hero.ToSpawn().Encode());          // los demas ven llegar al heroe
        log.LogInformation("cuenta {id} ({nombre}) entro al mapa {code}",
            join.Player.AccountId, join.Player.PilotName, map.Code);
    }

    /// <summary>Estado completo del mundo para un jugador: al entrar y al reconectar.</summary>
    private void SincronizarMundo(PlayerSlot slot)
    {
        var entrada = new EnterMap
        {
            MapId = (ulong)map.Id, MapCode = map.Code,
            LimitsX = map.BoundsX, LimitsY = map.BoundsY, CargoRiskPct = 100,
            StationX = map.StationX, StationY = map.StationY, StationRange = map.SecureRange,
        };
        // los portales van completos aqui: son mobiliario del mapa, no entidades
        foreach (var p in portals)
            entrada.Portals.Add(new MapPortal
            {
                PortalId = (ulong)p.Id, X = p.X, Y = p.Y,
                TargetMapCode = p.TargetMapCode, IsWorking = p.IsWorking,
            });
        slot.Port.Send(entrada.Encode());
        var precios = new NpcPrices();
        foreach (var p in npcPrices)
            precios.Prices.Add(new MaterialPrice
            {
                MaterialId = p.LootId, PriceCredits = (ulong)p.PriceCredits,
            });
        slot.Port.Send(precios.Encode());
        slot.Port.Send(slot.Entity.ToSpawn().Encode());
        slot.Port.Send(HeroStatsDe(slot).Encode());
        foreach (var otro in _players.Values)
            if (otro != slot)
                slot.Port.Send(otro.Entity.ToSpawn().Encode());
        foreach (var npc in _npcs.Values) slot.Port.Send(npc.ToSpawn().Encode());
        foreach (var caja in _boxes.Values)
            slot.Port.Send(new BoxSpawn
            {
                BoxId = caja.Id, BoxType = "from_ship",
                X = (ulong)Math.Round(caja.X), Y = (ulong)Math.Round(caja.Y),
            }.Encode());
        EnviarAlmacen(slot);
    }

    private void OnLeave(LeaveCmd leave)
    {
        var slot = _players.Values.FirstOrDefault(s => ReferenceEquals(s.Port, leave.Port));
        if (slot is null) return;
        // LOGOUT explicito = se va de verdad; una caida de socket abre la ventana
        // de gracia y la nave se queda en el mundo (auth-v1)
        if (leave.Reason == "LOGOUT")
        {
            Drop(slot, leave.Reason);
            return;
        }
        if (slot.Desconectado) return;
        slot.GraceUntilTick = _tick + GraceMs / tickMs;
        slot.LaserOn = false;
        log.LogInformation("cuenta {id}: socket caido, {s} s de gracia para reconectar",
            slot.Data.AccountId, GraceMs / 1000);
    }

    private void Drop(PlayerSlot slot, string reason)
    {
        _players.Remove(slot.Data.AccountId);
        Despawn(slot.Entity.Id, DespawnReason.Left);
        slot.Port.CloseSocket();
        var (id, mapId, x, y, hp, esc, sid) = (slot.Data.AccountId, map.Id,
            (uint)slot.Entity.X, (uint)slot.Entity.Y, slot.Entity.Hp, slot.Entity.Shield, slot.SessionId);
        _ = Task.Run(() => Safe(() =>
        {
            repo.SaveShipState(id, mapId, x, y, hp, esc);   // el estado siempre se persiste al salir
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
