// El mundo: UN hilo de simulacion con tick fijo. Todo estado vive aqui; las
// conexiones solo postean comandos al inbox y reciben eventos ya traducidos.
// La leccion del TickManager legado: ninguna excepcion de un tick puede matar
// el loop — cada tick esta blindado y el error se loguea con contexto.
//
// Esta clase esta partida en varios archivos por MOTIVO, no por tamaño:
//   · World.cs           el bucle, el estado y las tuberias
//   · World.Npcs.cs      la maquina de estados de la IA y su combate
//   · World.Combat.cs    el laser del jugador, la muerte y la reaparicion
//   · World.Cargo.cs     cajas, la base, descarga y venta
//   · World.Session.cs   entrar, volver, salir, moverse, hablar y saltar
//   · World.Relevance.cs el diff de visibilidad y la difusion dirigida
using System.Threading.Channels;
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World(MapInfo map, List<NpcSpawnInfo> npcSpawns,
    List<MaterialBias> zoneBias, RefineRecipe? refineRecipe, List<NpcPrice> npcPrices,
    List<PortalInfo> portals,
    IPlayerRepository players, ISessionRepository sessions, IEconomyRepository economy,
    IServerCodec codec, IClock clock, RelevanceRanges ranges, ILogger<World> log,
    int tickMs, int pingIntervalSeconds, int pingMissesToDrop, bool npcCombatEnabled = true)
{
    private readonly Channel<WorldCmd> _inbox = Channel.CreateUnbounded<WorldCmd>();

    /// <summary>Un jugador dentro de este mapa: su nave, su sesion y su socket.
    ///
    /// La nave y la carga son del dominio; el puerto y el heartbeat no lo son, y
    /// por eso este tipo vive en la capa de aplicacion y no en el centro.</summary>
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
        public bool AtStation;                              // dentro del rango de la estacion
        public string AmmoId = "ammo_cel_1";             // municion equipada (E3 la hara elegible)
        public bool Skilled;                             // disparo potenciado (perfil de piloto, E4)
        /// <summary>Ya se le dijo que su objetivo esta fuera del alcance del laser.
        /// Se avisa UNA vez por espera, no una vez por tick.</summary>
        public bool WarnedOutOfRange;
        /// <summary>Tick en que expira la gracia; long.MaxValue = socket vivo.</summary>
        public long GraceUntilTick = long.MaxValue;
        public bool Disconnected => GraceUntilTick != long.MaxValue;
        /// <summary>Destruido y esperando a elegir reaparicion: ni vuela ni dispara.</summary>
        public bool Dead;
        public uint CargoUsed => (uint)Cargo.Values.Sum(v => (long)v);
        /// <summary>Lo que el CLIENTE de este jugador cree que existe. La
        /// relevancia es un diff contra estos dos conjuntos.</summary>
        public readonly HashSet<ulong> SeenEntities = [];
        public readonly HashSet<ulong> SeenBoxes = [];
    }

    private readonly Dictionary<long, PlayerSlot> _players = new();     // account_id -> slot
    private readonly Dictionary<ulong, Entity> _npcs = new();
    private readonly Dictionary<ulong, NpcSpawnInfo> _npcInfo = new();  // entity_id -> catalogo
    private readonly Dictionary<ulong, NpcAi> _npcAi = new();           // entity_id -> su IA
    private readonly Dictionary<ulong, LootBox> _boxes = new();
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
    /// <summary>Los circulos de la estacion y de los portales — los mismos que el
    /// cliente pinta. Los NPC no entran salvo provocados.</summary>
    private readonly SafeZones _safe = SafeZones.Of(map, portals, Dials.JumpRange);
    private long _tick;

    public void Post(WorldCmd cmd) => _inbox.Writer.TryWrite(cmd);

    /// <summary>Los milisegundos de un dial, en ticks de este mundo.</summary>
    private int ToTicks(int ms) => ms / tickMs;

    /// <summary>Un paso de simulacion. Lo llama el <see cref="Universe"/>, que lleva
    /// UN bucle para todos los mapas: 29 temporizadores para 28 mapas vacios seria
    /// tirar el reloj a la basura.</summary>
    internal void Paso(double dt)
    {
        _tick++;
        try { Tick(dt); }
        catch (Exception ex)
        {
            // el tick JAMAS tumba el loop: se loguea y se sigue (leccion del legado)
            log.LogError(ex, "excepcion en tick {tick} del mapa {code}", _tick, map.Code);
        }
    }

    /// <summary>Un mapa sin jugadores NI comandos pendientes no necesita simularse:
    /// sus NPC no vagabundean para nadie.
    ///
    /// La segunda condicion no es un detalle. Mirando solo los jugadores, el mapa
    /// al que alguien acaba de entrar sigue vacio —el Join esta en la cola, sin
    /// procesar— asi que no se tickearia, asi que el Join no se procesaria nunca.
    /// Un mundo que se niega a despertar para atender lo que le despertaria.</summary>
    internal bool Idle => _players.Count == 0 && _inbox.Reader.Count == 0;

    /// <summary>El mapa que simula. Publico porque el host lo necesita para el
    /// `/health` y el log de arranque.</summary>
    public MapInfo Map => map;

    // ─── ventanas de inspeccion (solo para las pruebas; `internal`, no API) ──
    internal IReadOnlyDictionary<ulong, Entity> LiveNpcs => _npcs;
    internal Entity? ShipOf(long accountId) =>
        _players.TryGetValue(accountId, out var s) ? s.Entity : null;
    internal IReadOnlyCollection<ulong> LiveBoxes => _boxes.Keys;
    internal long CurrentTick => _tick;

    private void Tick(double dt)
    {
        while (_inbox.Reader.TryRead(out var cmd)) Handle(cmd);

        // NPCs: la maquina de estados del legado (vagabundear / perseguir / pegar)
        foreach (var npc in _npcs.Values.ToList())
        {
            ThinkNpc(npc);
            npc.Step(dt);
        }
        foreach (var slot in _players.Values)
        {
            slot.Entity.Step(dt);
            UpdateStationRange(slot);
        }

        FireLasers();
        ExpireBoxes();
        RespawnNpcs();
        SweepExpiredGraces();
        // el diff de visibilidad va al FINAL: para entonces todo se movio, se
        // murio y se reaparecio, asi que se calcula una sola vez sobre el
        // estado ya estable del tick
        UpdateRelevance();
        Heartbeat();
        WriteBehind();
    }

    private void Handle(WorldCmd cmd)
    {
        switch (cmd)
        {
            case JoinCmd join: OnJoin(join); break;
            case JumpCmd jump: OnJump(jump); break;
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
            case RespawnSelectCmd resp: OnRespawnSelect(resp); break;
        }
    }

    // ─── las fases del tick ─────────────────────────────────────────────────

    /// <summary>Laser encendido + objetivo vivo + en rango = un golpe por intervalo.</summary>
    private void FireLasers()
    {
        foreach (var slot in _players.Values)
        {
            if (!slot.LaserOn || _tick < slot.NextAttackTick) continue;
            if (!_npcs.TryGetValue(slot.TargetId, out var npc)) { slot.LaserOn = false; continue; }
            if (Geometry.Distance(npc, slot.Entity) > Dials.LaserRange)
            {
                // Fuera de rango el laser ESPERA, no se apaga. Pero esperar en
                // SILENCIO era el peor de los mundos: la pantalla mide 2198x1159
                // unidades y el laser alcanza 600, asi que mas de la mitad de lo
                // que se VE esta fuera de tiro. Pinchabas, disparabas, y no pasaba
                // nada — sin decir por que, y funcionando o no segun DONDE en la
                // pantalla estuviera el bicho.
                if (!slot.WarnedOutOfRange)
                {
                    slot.WarnedOutOfRange = true;
                    // requestId 0: no es la respuesta a nada que pidiera el
                    // cliente, es el server contandole algo por su cuenta
                    Send(slot, new Failed(0, ErrorCode.TooFar, "Fuera de alcance: acercate"));
                }
                continue;
            }
            slot.WarnedOutOfRange = false;
            slot.NextAttackTick = _tick + ToTicks(Dials.AttackIntervalMs);
            ApplyDamage(slot, npc);
        }
    }

    private void ExpireBoxes()
    {
        if (_tick % ToTicks(1_000) != 0) return;
        foreach (var box in _boxes.Values.Where(b => _tick >= b.ExpiraTick).ToList())
        {
            _boxes.Remove(box.Id);
            ToThoseWhoSeeBox(box.Id, new BoxDespawned(box.Id, BoxDespawnReason.Expired));
            ForgetBox(box.Id);
        }
    }

    private void RespawnNpcs()
    {
        foreach (var (when, info, id) in _respawns.Where(r => _tick >= r.Tick).ToList())
        {
            _respawns.Remove((when, info, id));
            // no se anuncia aqui: quien lo tenga cerca lo recibira en el paso de
            // relevancia de este mismo tick
            SpawnNpc(info, id);
        }
    }

    /// <summary>Gracia agotada: la nave sale del mundo y la sesion se cierra.</summary>
    private void SweepExpiredGraces()
    {
        foreach (var slot in _players.Values
                     .Where(s => s.Disconnected && _tick >= s.GraceUntilTick).ToList())
        {
            log.LogInformation("cuenta {id}: gracia agotada, saliendo del mundo", slot.Data.AccountId);
            Drop(slot, "TIMEOUT");
        }
    }

    /// <summary>Ping con nonce; N sin respuesta = socket muerto. No se dropea: se
    /// cierra el socket y empieza la cuenta atras de la gracia.</summary>
    private void Heartbeat()
    {
        var pingCadaTicks = pingIntervalSeconds * 1000 / tickMs;
        foreach (var slot in _players.Values.ToList())
        {
            if (slot.Disconnected) continue;      // sin socket no hay a quien pingear
            if (_tick - slot.LastPingTick < pingCadaTicks) continue;
            if (slot.PingMisses >= pingMissesToDrop)
            {
                log.LogInformation("cuenta {id}: {n} pings sin respuesta, abriendo gracia",
                    slot.Data.AccountId, slot.PingMisses);
                slot.Port.CloseSocket();
                slot.GraceUntilTick = _tick + ToTicks(Dials.GraceMs);
                slot.LaserOn = false;
                continue;
            }
            slot.PingNonce = (ulong)_rng.NextInt64(1, long.MaxValue);
            slot.PingMisses++;
            slot.LastPingTick = _tick;
            Send(slot, new Pinged(slot.PingNonce));
        }
    }

    /// <summary>Persistencia diferida del estado en vivo, fuera del hilo del tick.</summary>
    private void WriteBehind()
    {
        if (_tick % ToTicks(Dials.WriteBehindMs) != 0) return;
        foreach (var slot in _players.Values)
        {
            SaveShip(slot);
            var sid = slot.SessionId;
            _ = Task.Run(() => Safe(() => sessions.TouchSession(sid), "TouchSession"));
        }
    }

    // ─── tuberias ───────────────────────────────────────────────────────────

    /// <summary>Un evento a un jugador. El codec traduce; el mundo no sabe a que.</summary>
    private void Send(PlayerSlot slot, ServerEvent serverEvent) => slot.Port.Send(codec.Encode(serverEvent));

    private void Send(IClientPort port, ServerEvent serverEvent) => port.Send(codec.Encode(serverEvent));

    private void Despawn(ulong entityId, DespawnReason reason)
    {
        ToThoseWhoSee(entityId, new EntityDespawned(entityId, reason));
        ForgetEntity(entityId);
    }

    private PlayerSlot? SlotOf(IClientPort port) =>
        _players.Values.FirstOrDefault(s => ReferenceEquals(s.Port, port));

    private HeroStatsUpdated HeroStatsOf(PlayerSlot slot) => new(
        slot.Entity.Hp, slot.Entity.MaxHp, slot.Entity.Shield, slot.Entity.MaxShield,
        slot.CargoUsed, slot.Data.CargoCapacity, (ulong)slot.Credits, 0, 1);

    /// <summary>La UNICA escritura caliente del server (esquema-v1 §5). Se copian
    /// los valores ANTES de soltar la tarea: leer el slot desde otro hilo seria
    /// justo la clase de carrera que este server no puede permitirse.</summary>
    private void SaveShip(PlayerSlot slot)
    {
        var (id, mapId, x, y, hp, esc) = (slot.Data.AccountId, map.Id,
            (uint)slot.Entity.X, (uint)slot.Entity.Y, slot.Entity.Hp, slot.Entity.Shield);
        _ = Task.Run(() => Safe(() => players.SaveShipState(id, mapId, x, y, hp, esc), "SaveShipState"));
    }

    private void Safe(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { log.LogError(ex, "fallo en {que}", what); }
    }
}
