// El banco de pruebas: un World de verdad, con la BD y el socket sustituidos por
// dobles. Todo lo que se afirma en las pruebas entra por los mismos caminos que
// usa el juego —comandos al inbox, frames del protocolo hacia el cliente— asi
// que caracterizan COMPORTAMIENTO, no estructura interna.
//
// Es deliberado que sobrevivan al refactor: cuando el World se parta en capas,
// aqui solo cambia el cableado del constructor; las afirmaciones siguen valiendo.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace MexOrbit.GameServer.Tests;

/// <summary>Un cliente que no habla por un socket: guarda los frames tal cual
/// salen del mundo, ya codificados.</summary>
public sealed class PuertoFalso(long accountId) : IClientPort
{
    // El mundo no escribe solo desde el hilo del tick: `EnviarAlmacen` manda
    // desde un `Task.Run`. Una `List` sin candado ahi dentro es una carrera de
    // verdad, y aparecia como pruebas que fallaban una de cada tres veces.
    private readonly List<byte[]> _frames = [];

    public long AccountId { get; } = accountId;
    public bool Cerrado { get; private set; }

    public IReadOnlyList<byte[]> Frames { get { lock (_frames) return [.. _frames]; } }

    public void Send(byte[] frame) { lock (_frames) _frames.Add(frame); }
    public void CloseSocket() => Cerrado = true;

    public void Limpiar() { lock (_frames) _frames.Clear(); }

    /// <summary>Todos los mensajes de un tipo recibidos, en orden.</summary>
    public List<T> Todos<T>() where T : class => Frames
        .Where(f => Protocolo.MsgIdDe(f) == Protocolo.IdDe<T>())
        .Select(Protocolo.Decodificar<T>)
        .ToList();

    public T Ultimo<T>() where T : class
    {
        var todos = Todos<T>();
        Assert.NotEmpty(todos);
        return todos[^1];
    }

    public T? UltimoOrNull<T>() where T : class
    {
        var todos = Todos<T>();
        return todos.Count == 0 ? null : todos[^1];
    }

    public bool Recibio<T>() where T : class => Todos<T>().Count > 0;
}

/// <summary>Tabla msg_id -> decodificador. A mano y no por reflexion: `Decode`
/// toma un `ReadOnlySpan<byte>`, que es un ref struct y no cabe en un `object`.</summary>
public static class Protocolo
{
    private static readonly Dictionary<Type, (int Id, Func<byte[], object> Dec)> Tabla = new()
    {
        [typeof(Welcome)] = (Welcome.MsgId, f => Welcome.Decode(f)),
        [typeof(ResumeOk)] = (ResumeOk.MsgId, f => ResumeOk.Decode(f)),
        [typeof(Ping)] = (Ping.MsgId, f => Ping.Decode(f)),
        [typeof(ErrorReply)] = (ErrorReply.MsgId, f => ErrorReply.Decode(f)),
        [typeof(SessionReplaced)] = (SessionReplaced.MsgId, f => SessionReplaced.Decode(f)),
        [typeof(EnterMap)] = (EnterMap.MsgId, f => EnterMap.Decode(f)),
        [typeof(EntitySpawn)] = (EntitySpawn.MsgId, f => EntitySpawn.Decode(f)),
        [typeof(EntityDespawn)] = (EntityDespawn.MsgId, f => EntityDespawn.Decode(f)),
        [typeof(EntityMove)] = (EntityMove.MsgId, f => EntityMove.Decode(f)),
        [typeof(HeroStats)] = (HeroStats.MsgId, f => HeroStats.Decode(f)),
        [typeof(TargetInfo)] = (TargetInfo.MsgId, f => TargetInfo.Decode(f)),
        [typeof(AttackEvent)] = (AttackEvent.MsgId, f => AttackEvent.Decode(f)),
        [typeof(EntityDestroyed)] = (EntityDestroyed.MsgId, f => EntityDestroyed.Decode(f)),
        [typeof(RespawnOptions)] = (RespawnOptions.MsgId, f => RespawnOptions.Decode(f)),
        [typeof(BoxSpawn)] = (BoxSpawn.MsgId, f => BoxSpawn.Decode(f)),
        [typeof(BoxDespawn)] = (BoxDespawn.MsgId, f => BoxDespawn.Decode(f)),
        [typeof(CollectResult)] = (CollectResult.MsgId, f => CollectResult.Decode(f)),
        [typeof(StorageState)] = (StorageState.MsgId, f => StorageState.Decode(f)),
        [typeof(SellResult)] = (SellResult.MsgId, f => SellResult.Decode(f)),
        [typeof(UnloadResult)] = (UnloadResult.MsgId, f => UnloadResult.Decode(f)),
        [typeof(NpcPrices)] = (NpcPrices.MsgId, f => NpcPrices.Decode(f)),
        [typeof(StationRange)] = (StationRange.MsgId, f => StationRange.Decode(f)),
        [typeof(JumpHandoff)] = (JumpHandoff.MsgId, f => JumpHandoff.Decode(f)),
        [typeof(ChatMessage)] = (ChatMessage.MsgId, f => ChatMessage.Decode(f)),
    };

    public static int IdDe<T>() => Tabla[typeof(T)].Id;

    public static T Decodificar<T>(byte[] frame) where T : class => (T)Tabla[typeof(T)].Dec(frame);

    /// <summary>El varint de cabecera, igual que lo lee la conexion real.</summary>
    public static int MsgIdDe(byte[] frame)
    {
        int id = 0, shift = 0, pos = 0;
        while (pos < frame.Length && pos < 4)
        {
            var b = frame[pos++];
            id |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return id;
            shift += 7;
        }
        return -1;
    }
}

/// <summary>La BD que no existe: guarda en diccionarios lo que el mundo escribe,
/// para poder afirmar QUE se persistio sin levantar MySQL.</summary>
public sealed class BdFalsa : IPlayerRepository, ISessionRepository, IEconomyRepository
{
    public readonly List<(long AccountId, long MapId, uint X, uint Y, uint Hp, uint Shield)> Guardados = [];
    public readonly List<(long SessionId, string Reason)> Cerradas = [];
    public readonly List<(long AccountId, decimal Delta, string Reason, long? Ref)> Creditos = [];
    public readonly List<(long AccountId, long BoxRef)> CargasVaciadas = [];
    public readonly List<(long AccountId, List<(long ItemId, uint Amount)> Items, long BoxRef)> Recogidas = [];
    public Dictionary<long, uint> Bodega = [];
    public UnloadOutcome ProximaDescarga = new(new Dictionary<long, uint>(), new Dictionary<long, uint>());
    public (uint Sold, decimal Gained, decimal NewCredits) ProximaVenta = (0, 0m, 0m);
    public List<(string LootId, decimal Amount)> Almacen = [];
    /// <summary>Para probar que un fallo de BD no rompe el mundo ni miente al cliente.</summary>
    public bool RevientaAlEscribir;
    /// <summary>Para probar que el write-behind puede reventar sin matar el tick.</summary>
    public bool RevientaAlGuardar;

    public PlayerData? LoadPlayer(long accountId) => null;
    public uint LoadLaserDamage(long accountId) => 0;
    public uint LoadShieldCapacity(long accountId) => 0;
    public Dictionary<long, uint> LoadCargo(long accountId) => new(Bodega);

    public void SaveShipState(long accountId, long mapId, uint x, uint y, uint hp, uint shield)
    {
        if (RevientaAlGuardar) throw new InvalidOperationException("BD caida");
        lock (Guardados) Guardados.Add((accountId, mapId, x, y, hp, shield));
    }

    public (long SessionId, string ReconnectToken) OpenSession(long accountId) => (1, "token");
    public (long SessionId, long AccountId)? FindSessionByToken(string token) => null;
    public void CloseSession(long sessionId, string reason) { lock (Cerradas) Cerradas.Add((sessionId, reason)); }
    public void TouchSession(long sessionId) { }

    public void AddCargoPickup(long accountId, IEnumerable<(long ItemId, uint Amount)> items, long boxRef)
    {
        if (RevientaAlEscribir) throw new InvalidOperationException("BD caida");
        lock (Recogidas) Recogidas.Add((accountId, items.ToList(), boxRef));
    }

    public void ClearCargo(long accountId, long boxRef)
    {
        lock (CargasVaciadas) CargasVaciadas.Add((accountId, boxRef));
    }

    public void AddCredits(long accountId, decimal delta, string reason, long? refId = null)
    {
        lock (Creditos) Creditos.Add((accountId, delta, reason, refId));
    }

    public List<(string LootId, decimal Amount)> LoadStorage(long accountId) => Almacen;

    public UnloadOutcome UnloadAndRefine(long accountId, RefineRecipe? receta)
    {
        if (RevientaAlEscribir) throw new InvalidOperationException("BD caida");
        return ProximaDescarga;
    }

    public (uint Sold, decimal Gained, decimal NewCredits) SellToNpc(
        long accountId, long itemId, uint amount, decimal price)
    {
        if (RevientaAlEscribir) throw new InvalidOperationException("BD caida");
        return ProximaVenta;
    }
}
