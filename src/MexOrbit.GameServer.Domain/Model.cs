// El modelo del juego: mapas, catalogos y el piloto.
//
// Estos tipos vivian en `Data/Repo.cs`, junto a las consultas que los llenaban.
// Eso invertia la flecha: las reglas del juego dependian del acceso a datos.
// Aqui son lo que siempre fueron —el vocabulario del dominio— y es la BD la que
// tiene que saber producirlos, no al reves.
namespace MexOrbit.GameServer.Domain;

/// <summary>Un sector: sus limites, su estacion y su zona.</summary>
public sealed record MapInfo(long Id, string Code, string DisplayName, uint BoundsX, uint BoundsY,
    uint StationX, uint StationY, uint SecureRange, string ZoneTier);

/// <summary>Un portal visible del mapa. Viaja completo al entrar: es mobiliario,
/// no una entidad que entre por relevancia.</summary>
public sealed record PortalInfo(long Id, uint X, uint Y, string TargetMapCode, bool IsWorking,
    uint TargetX, uint TargetY);

/// <summary>Donde vive un mapa. Hoy todas las filas apuntan al mismo sitio; el
/// codigo no lo sabe, y por eso partirlos manana es cambiar filas.</summary>
public sealed record MapServer(string Host, ushort Port, bool IsTls);

/// <summary>El sesgo de drops de la zona: la mezcla la fija la zona, el NPC la cantidad.</summary>
public sealed record MaterialBias(long ItemId, string LootId, decimal Weight);

public sealed record RefineRecipe(long OutputItemId, string OutputLootId, uint OutputAmount,
    Dictionary<long, uint> Ingredients);

public sealed record NpcPrice(long ItemId, string LootId, decimal PriceCredits);

/// <summary>Resultado de descargar: lo que entro al almacen y lo que produjo el refinado.</summary>
public sealed record UnloadOutcome(Dictionary<long, uint> Stored, Dictionary<long, uint> Refined);

public sealed record NpcSpawnInfo(long CatalogId, string Code, string DisplayName, uint MaxHp,
    uint MaxShield, ushort Speed, uint Damage, bool IsAggressive, byte FleeHpPct, uint AggroRadius,
    uint RespawnSeconds, ushort Amount, uint RewardExperience, uint RewardHonor, uint RewardCredits,
    uint CargoDropMin, uint CargoDropMax);

/// <summary>La posicion va CON SIGNO: la zona radiactiva por el lado del 0 es
/// negativa, y se persiste tal cual (player_ship_state.pos_x/pos_y son INT).</summary>
public sealed record PlayerData(long AccountId, string PilotName, byte Faction, string ShipCode,
    uint BaseHp, ushort BaseSpeed, uint CargoCapacity, uint CurrentHp, uint CurrentShield,
    int PosX, int PosY, decimal Credits, long MapId);

/// <summary>Una caja en el suelo: lo que solto un NPC al caer, o la bodega volante
/// de un jugador destruido. Transferencia, no destruccion (guidelines §7).</summary>
public sealed class LootBox
{
    public required ulong Id { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    /// <summary>server_item_id -> unidades.</summary>
    public required Dictionary<long, uint> Drops { get; init; }
    public required long ExpiraTick { get; init; }
}

/// <summary>Un material y su cantidad, por su `loot_id` publico (el id interno
/// `server_item_id` jamas sale del server).</summary>
public sealed record MaterialAmount(string MaterialId, uint Amount);
