// Los puertos: lo que la simulacion NECESITA del mundo exterior, dicho en su
// idioma. Ni una sola de estas interfaces menciona MySQL, WebSockets ni varints.
//
// Estan partidos por MOTIVO, no por tabla: quien lee catalogos al arrancar un
// mapa no tiene nada que ver con quien mueve credits dentro de una transaccion.
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Application;

// ─── persistencia ───────────────────────────────────────────────────────────

/// <summary>Los mapas y su topologia: donde se entra, que hay, quien lo sirve.</summary>
public interface IMapCatalog
{
    MapInfo LoadStarterMap();
    MapInfo? LoadMap(string code);
    MapInfo? LoadMapById(long mapId);
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
    bool LoadBoolSetting(string key, bool fallback);
    int LoadIntSetting(string key, int fallback);
}

/// <summary>El jugador y su nave: lo que se lee al entrar y la unica escritura
/// caliente del server (esquema-v1 §5).</summary>
public interface IPlayerRepository
{
    PlayerData? LoadPlayer(long accountId);
    uint LoadLaserDamage(long accountId);
    uint LoadShieldCapacity(long accountId);
    Dictionary<long, uint> LoadCargo(long accountId);
    void SaveShipState(long accountId, long mapId, int x, int y, uint hp, uint shield);
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
    UnloadOutcome UnloadAndRefine(long accountId, RefineRecipe? recipe);
    (uint Sold, decimal Gained, decimal NewCredits) SellToNpc(
        long accountId, long itemId, uint amount, decimal price);
}

// ─── el cliente ─────────────────────────────────────────────────────────────

/// <summary>Un cliente conectado, visto desde dentro: una cuenta, un buzon de
/// salida y una forma de colgar. El mundo NO sabe si detras hay un WebSocket.</summary>
public interface IClientPort
{
    long AccountId { get; }
    void Send(byte[] frame);
    void CloseSocket();
}

/// <summary>El traductor al cable.
///
/// Devuelve el frame ya montado para que el broadcast codifique UNA vez y mande
/// el MISMO array a todos: sacar el protocolo del dominio no puede costar N
/// serializaciones por evento.</summary>
public interface IServerCodec
{
    byte[] Encode(ServerEvent serverEvent);
}

/// <summary>Verificacion del game ticket emitido por la api. El game server solo
/// tiene la clave PUBLICA: no puede emitir, solo validar.</summary>
public interface ITicketVerifier
{
    (long AccountId, ErrorCode? Error) Verify(string jwt, int expectedProtocolVersion);
}

/// <summary>El reloj de pared, inyectado. Cero `DateTime.Now` disperso: es uno de
/// los nueve vicios documentados del server legado.</summary>
public interface IClock
{
    long UnixMs { get; }
}
