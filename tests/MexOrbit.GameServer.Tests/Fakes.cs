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
public sealed class FakePort(long accountId) : IClientPort
{
    // El mundo no escribe solo desde el hilo del tick: `SendStorage` manda
    // desde un `Task.Run`. Una `List` sin candado ahi dentro es una carrera de
    // verdad, y aparecia como pruebas que fallaban una de cada tres veces.
    private readonly List<byte[]> _frames = [];

    public long AccountId { get; } = accountId;
    public bool Closed { get; private set; }

    public IReadOnlyList<byte[]> Frames { get { lock (_frames) return [.. _frames]; } }

    public void Send(byte[] frame) { lock (_frames) _frames.Add(frame); }
    public void CloseSocket() => Closed = true;

    public void Clear() { lock (_frames) _frames.Clear(); }

    /// <summary>All los mensajes de un tipo recibidos, en orden.</summary>
    public List<T> All<T>() where T : class => Frames
        .Where(f => Wire.MsgIdOf(f) == Wire.IdOf<T>())
        .Select(Wire.DecodeFrame<T>)
        .ToList();

    public T Last<T>() where T : class
    {
        var todos = All<T>();
        Assert.NotEmpty(todos);
        return todos[^1];
    }

    public T? LastOrNull<T>() where T : class
    {
        var todos = All<T>();
        return todos.Count == 0 ? null : todos[^1];
    }

    public bool Received<T>() where T : class => All<T>().Count > 0;
}

/// <summary>Table msg_id -> decodificador. A mano y no por reflexion: `Decode`
/// toma un `ReadOnlySpan<byte>`, que es un ref struct y no cabe en un `object`.</summary>
public static class Wire
{
    private static readonly Dictionary<Type, (int Id, Func<byte[], object> Dec)> Table = new()
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

    public static int IdOf<T>() => Table[typeof(T)].Id;

    public static T DecodeFrame<T>(byte[] frame) where T : class => (T)Table[typeof(T)].Dec(frame);

    /// <summary>El varint de cabecera, igual que lo lee la conexion real.</summary>
    public static int MsgIdOf(byte[] frame)
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
public sealed class FakeDb : IPlayerRepository, ISessionRepository, IEconomyRepository
{
    public readonly List<(long AccountId, long MapId, uint X, uint Y, uint Hp, uint Shield)> SavedStates = [];
    public readonly List<(long SessionId, string Reason)> ClosedSessions = [];
    public readonly List<(long AccountId, decimal Delta, string Reason, long? Ref)> CreditEntries = [];
    public readonly List<(long AccountId, long BoxRef)> ClearedCargo = [];
    public readonly List<(long AccountId, List<(long ItemId, uint Amount)> Items, long BoxRef)> Pickups = [];
    public Dictionary<long, uint> Hold = [];
    public UnloadOutcome NextUnload = new(new Dictionary<long, uint>(), new Dictionary<long, uint>());
    public (uint Sold, decimal Gained, decimal NewCredits) NextSale = (0, 0m, 0m);
    public List<(string LootId, decimal Amount)> EncodeStorage = [];
    /// <summary>Para probar que un fallo de BD no rompe el mundo ni miente al cliente.</summary>
    public bool FailsOnWrite;
    /// <summary>Para probar que el write-behind puede reventar sin matar el tick.</summary>
    public bool FailsOnSave;

    public PlayerData? LoadPlayer(long accountId) => null;
    public uint LoadLaserDamage(long accountId) => 0;
    public uint LoadShieldCapacity(long accountId) => 0;
    public Dictionary<long, uint> LoadCargo(long accountId) => new(Hold);

    public void SaveShipState(long accountId, long mapId, uint x, uint y, uint hp, uint shield)
    {
        if (FailsOnSave) throw new InvalidOperationException("BD caida");
        lock (SavedStates) SavedStates.Add((accountId, mapId, x, y, hp, shield));
    }

    public (long SessionId, string ReconnectToken) OpenSession(long accountId) => (1, "token");
    public (long SessionId, long AccountId)? FindSessionByToken(string token) => null;
    public void CloseSession(long sessionId, string reason) { lock (ClosedSessions) ClosedSessions.Add((sessionId, reason)); }
    public void TouchSession(long sessionId) { }

    public void AddCargoPickup(long accountId, IEnumerable<(long ItemId, uint Amount)> items, long boxRef)
    {
        if (FailsOnWrite) throw new InvalidOperationException("BD caida");
        lock (Pickups) Pickups.Add((accountId, items.ToList(), boxRef));
    }

    public void ClearCargo(long accountId, long boxRef)
    {
        lock (ClearedCargo) ClearedCargo.Add((accountId, boxRef));
    }

    public void AddCredits(long accountId, decimal delta, string reason, long? refId = null)
    {
        lock (CreditEntries) CreditEntries.Add((accountId, delta, reason, refId));
    }

    public List<(string LootId, decimal Amount)> LoadStorage(long accountId) => EncodeStorage;

    public UnloadOutcome UnloadAndRefine(long accountId, RefineRecipe? recipe)
    {
        if (FailsOnWrite) throw new InvalidOperationException("BD caida");
        return NextUnload;
    }

    public (uint Sold, decimal Gained, decimal NewCredits) SellToNpc(
        long accountId, long itemId, uint amount, decimal price)
    {
        if (FailsOnWrite) throw new InvalidOperationException("BD caida");
        return NextSale;
    }
}
