// Lo que el mundo tiene que CONTAR. Antes esto no existia: la simulacion armaba
// mensajes del protocolo y llamaba a `.Encode()` en mitad de las reglas de
// combate, asi que no se podia razonar sobre el juego sin el contrato binario
// delante ni cambiar el wire sin tocar el juego.
//
// Ahora el mundo emite hechos y el codec los pone en el cable. Un evento nuevo se
// añade aqui; el dia que el traductor no sepa traducirlo, no compila.
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Application;

public abstract record ServerEvent;

// ─── entidades ──────────────────────────────────────────────────────────────

public sealed record EntitySpawned(Entity Entity) : ServerEvent;
public sealed record EntityMoved(Entity Entity, bool Teleport = false) : ServerEvent;
public sealed record EntityDespawned(ulong EntityId, DespawnReason Reason) : ServerEvent;
public sealed record EntityDestroyed(ulong EntityId, ulong KillerId) : ServerEvent;

public sealed record AttackLanded(ulong AttackerId, ulong TargetId, Weapon Weapon, uint Damage,
    uint TargetHp, uint TargetShield, bool Missed, string AmmoId, bool Skilled) : ServerEvent;

// ─── cajas ──────────────────────────────────────────────────────────────────

public sealed record BoxSpawned(ulong BoxId, string BoxType, double X, double Y) : ServerEvent;
public sealed record BoxDespawned(ulong BoxId, BoxDespawnReason Reason) : ServerEvent;

// ─── el mapa y el heroe ─────────────────────────────────────────────────────

public sealed record MapEntered(MapInfo Map, IReadOnlyList<PortalInfo> Portals,
    uint CargoRiskPct) : ServerEvent;

public sealed record PricesPublished(IReadOnlyList<NpcPrice> Prices) : ServerEvent;

public sealed record HeroStatsUpdated(uint Hp, uint MaxHp, uint Shield, uint MaxShield,
    uint Cargo, uint MaxCargo, ulong Credits, ulong Experience, uint Level) : ServerEvent;

public sealed record TargetAcquired(ulong EntityId, uint Hp, uint MaxHp,
    uint Shield, uint MaxShield) : ServerEvent;

public sealed record StationRangeChanged(bool InRange, ulong StationId) : ServerEvent;

public sealed record RespawnOffered(DeathCause Cause, string KillerName,
    IReadOnlyList<RespawnChoice> Options) : ServerEvent;

public sealed record RespawnChoice(ulong OptionId, string LabelKey, ulong CostCredits, bool Available);

// ─── bodega, almacen y economia ─────────────────────────────────────────────

public sealed record Collected(ulong RequestId, IReadOnlyList<MaterialAmount> Drops) : ServerEvent;

public sealed record Unloaded(ulong RequestId, IReadOnlyList<MaterialAmount> Stored,
    IReadOnlyList<MaterialAmount> Refined) : ServerEvent;

public sealed record Sold(ulong RequestId, ulong CreditsGained, ulong NewCredits) : ServerEvent;

public sealed record StorageSynced(IReadOnlyList<MaterialAmount> Materials) : ServerEvent;

// ─── sesion y chat ──────────────────────────────────────────────────────────

public sealed record Welcomed(long AccountId, string ReconnectToken, ulong ServerTimeMs,
    uint TickRate) : ServerEvent;

public sealed record ResumeAccepted : ServerEvent;
public sealed record SessionTakenOver : ServerEvent;
public sealed record Pinged(ulong Nonce) : ServerEvent;
public sealed record Failed(ulong RequestId, ErrorCode Code, string Detail = "") : ServerEvent;

public sealed record ChatBroadcast(ChatChannel Channel, string FromName, string FromClan,
    string Text, ulong ServerTimeMs) : ServerEvent;

public sealed record JumpHandedOff(string MapCode, MapServer Server) : ServerEvent;
