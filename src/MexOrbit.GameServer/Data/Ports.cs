// Los puertos de persistencia: lo que la simulacion NECESITA saber de la BD,
// dicho en su idioma y no en el de MySQL.
//
// Antes `World` y `Universe` recibian el `Repo` concreto —sellado, con Dapper y
// MySqlConnector dentro— asi que no habia forma de simular un tick sin una base
// de datos delante. La cebolla exige que la flecha apunte al reves: la capa de
// dentro declara el contrato y la de fuera lo cumple.
//
// Estan partidos por MOTIVO, no por tabla: quien lee catalogos al arrancar un
// mapa no tiene nada que ver con quien mueve credits dentro de una transaccion.
namespace MexOrbit.GameServer.Data;

/// <summary>Los mapas y su topologia: donde se entra, que hay, quien lo sirve.</summary>
public interface IMapCatalog
{
    MapInfo LoadStarterMap();
    MapInfo? LoadMap(string code);
    MapInfo? LoadMapPorId(long mapId);
    MapServer? LoadMapServer(long mapId);
    List<PortalInfo> LoadPortals(long mapId);
}

/// <summary>Los catalogos de JUEGO que definen como se comporta un mapa: que
/// bichos lo pueblan, que suelta la zona, que compra el NPC, que se refina.</summary>
public interface IGameCatalog
{
    List<NpcSpawnInfo> LoadNpcSpawns(long mapId);
    List<MaterialBias> LoadZoneBias(string zoneTier);
    RefineRecipe? LoadRefineRecipe();
    List<NpcPrice> LoadNpcPrices();
}

/// <summary>Los diales de JUEGO que viven en BD con su auditoria, no en appsettings.</summary>
public interface IServerSettings
{
    bool LoadBoolSetting(string key, bool porDefecto);
}

/// <summary>El jugador y su nave: lo que se lee al entrar y la unica escritura
/// caliente del server (esquema-v1 §5).</summary>
public interface IPlayerRepository
{
    PlayerData? LoadPlayer(long accountId);
    uint LoadLaserDamage(long accountId);
    uint LoadShieldCapacity(long accountId);
    Dictionary<long, uint> LoadCargo(long accountId);
    void SaveShipState(long accountId, long mapId, uint x, uint y, uint hp, uint shield);
}

/// <summary>Sesion unica por cuenta: abrir expulsando, resolver el token de
/// reconexion, cerrar con motivo.</summary>
public interface ISessionRepository
{
    (long SessionId, string ReconnectToken) OpenSession(long accountId);
    (long SessionId, long AccountId)? FindSessionByToken(string token);
    void CloseSession(long sessionId, string reason);
    void TouchSession(long sessionId);
}

/// <summary>Todo lo que mueve material o credits. Siempre relativo y siempre con
/// su asiento en el ledger (esquema-v1 §4).</summary>
public interface IEconomyRepository
{
    void AddCargoPickup(long accountId, IEnumerable<(long ItemId, uint Amount)> items, long boxRef);
    void ClearCargo(long accountId, long boxRef);
    void AddCredits(long accountId, decimal delta, string reason, long? refId = null);
    List<(string LootId, decimal Amount)> LoadStorage(long accountId);
    UnloadOutcome UnloadAndRefine(long accountId, RefineRecipe? receta);
    (uint Sold, decimal Gained, decimal NewCredits) SellToNpc(
        long accountId, long itemId, uint amount, decimal price);
}
