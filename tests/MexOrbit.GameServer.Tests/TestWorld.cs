// El armador: un World listo para jugar en tres lineas, con los mismos diales
// que corren en produccion (tick de 80 ms) y catalogos minimos pero reales.
//
// El heartbeat viene APAGADO por defecto (intervalo de una hora). No es pereza:
// con los 10 s reales, cualquier prueba que tickee mas de 30 s vería su jugador
// expulsado por no contestar unos Pong que la prueba no esta ejercitando. Quien
// caracteriza el heartbeat lo enciende con `WithPing`, y ahi si son los diales
// de verdad.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.GameServer.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace MexOrbit.GameServer.Tests;

public sealed class TestWorld
{
    public const int TickMs = 80;
    public const double Dt = TickMs / 1000.0;
    /// <summary>Ticks que caben en un segundo con el tick de 80 ms.</summary>
    public const int PerSecond = 1000 / TickMs;

    public FakeDb Bd { get; } = new();
    public MapInfo Map { get; private set; } =
        new(1, "1-1", "Sector 1-1", 20800, 12800, 10_000, 6_000, 1_500, "core");

    private readonly List<NpcSpawnInfo> _spawns = [];
    private List<MaterialBias> _bias = [new(10, "asterium", 1m)];
    private RefineRecipe? _receta;
    private List<NpcPrice> _precios = [new(10, "asterium", 5m)];
    private readonly List<PortalInfo> _portales = [];
    private readonly Dictionary<long, (double X, double Y, uint Speed)> _naves = [];
    private readonly Dictionary<long, ulong> _seq = [];
    private bool _combateNpc = true;
    private RelevanceRanges _rangos = RelevanceRanges.Fallback;
    private int _pingSegundos = 3_600;
    private int _pingFallos = 3;
    private World? _world;

    public World W => _world ?? throw new InvalidOperationException("llama a Build() primero");

    // ─── armado ─────────────────────────────────────────────────────────────

    public TestWorld WithMap(uint boundsX, uint boundsY, uint stationX, uint stationY, uint secureRange)
    {
        Map = Map with
        {
            BoundsX = boundsX, BoundsY = boundsY,
            StationX = stationX, StationY = stationY, SecureRange = secureRange,
        };
        return this;
    }

    /// <summary>Un bicho de laboratorio: quieto (velocidad 0) y ciego (aggro 0),
    /// asi que se queda donde el sorteo lo puso y no persigue a nadie. Es el
    /// unico modo de tener un blanco fijo sin tocar el estado privado del mundo.</summary>
    public TestWorld WithDummy(uint hp = 1_000, uint shield = 0, uint dropMin = 30, uint dropMax = 30,
        uint reward = 100, ushort howMany = 1) =>
        WithNpc(new NpcSpawnInfo(1, "vex", "Vex", hp, shield, 0, 50, false, 0, 0, 30, howMany,
            0, 0, reward, dropMin, dropMax));

    public TestWorld WithNpc(NpcSpawnInfo spawn) { _spawns.Add(spawn); return this; }
    public TestWorld WithPortal(PortalInfo portal) { _portales.Add(portal); return this; }
    public TestWorld WithRecipe(RefineRecipe recipe) { _receta = recipe; return this; }
    public TestWorld WithBias(params MaterialBias[] bias) { _bias = [.. bias]; return this; }
    public TestWorld WithPrices(params NpcPrice[] prices) { _precios = [.. prices]; return this; }
    public TestWorld WithoutNpcCombat() { _combateNpc = false; return this; }

    /// <summary>Los rangos de relevancia. Por defecto los de la spec (2000 / 1250
    /// con 10% de histeresis); una prueba que quiera ver el mapa entero pone
    /// `WithoutRelevance()`.</summary>
    public TestWorld WithRanges(double entidades, double objetos, byte histeresisPct = 10)
    {
        _rangos = new RelevanceRanges(entidades, objetos, histeresisPct);
        return this;
    }

    /// <summary>Rango practicamente infinito: para las pruebas que caracterizan
    /// OTRA cosa y no quieren que la visibilidad les mueva el suelo.</summary>
    public TestWorld WithoutRelevance() => WithRanges(1_000_000, 1_000_000, 0);

    /// <summary>Enciende el heartbeat con sus diales reales.</summary>
    public TestWorld WithPing(int segundos = 10, int fallos = 3)
    {
        _pingSegundos = segundos;
        _pingFallos = fallos;
        return this;
    }

    public TestWorld Build()
    {
        // El codec es el DE VERDAD: las pruebas afirman sobre los mismos bytes que
        // recibiria el cliente de Godot, no sobre un doble complaciente.
        _world = new World(Map, _spawns, _bias, _receta, _precios, _portales,
            Bd, Bd, Bd, new ServerCodec(), new FixedClock(), _rangos, NullLogger<World>.Instance,
            TickMs, _pingSegundos, _pingFallos, _combateNpc);
        _world.SpawnNpcs();
        return this;
    }

    // ─── el reloj ───────────────────────────────────────────────────────────

    public TestWorld Tick(int times = 1)
    {
        for (var i = 0; i < times; i++) W.Paso(Dt);
        return this;
    }

    /// <summary>Ticks exactos para esos segundos. Con `PerSecond` (division
    /// entera: 12) se perdia medio tick por segundo, y a los 150 s de una caja
    /// eso son 4 s de menos: la prueba miraba antes de que el plazo venciera.</summary>
    public TestWorld Seconds(double s) => Tick((int)Math.Ceiling(s * 1000 / TickMs));

    /// <summary>Tickea hasta que se cumpla la condicion. Para lo que depende del
    /// sorteo (un NPC que arranca a vagabundear) y no de un plazo fijo.</summary>
    public TestWorld TickUntil(Func<bool> condition, int limit = 200, string what = "la condicion")
    {
        for (var i = 0; i < limit; i++)
        {
            if (condition()) return this;
            Tick();
        }
        if (!condition()) throw new InvalidOperationException($"nunca se cumplio: {what}");
        return this;
    }

    // ─── jugadores ──────────────────────────────────────────────────────────

    public PlayerData Pilot(long accountId, string nombre = "Pilot", byte faction = 1,
        uint hp = 4_000, ushort speed = 320, uint hold = 300,
        uint x = 10_000, uint y = 6_000) =>
        new(accountId, nombre, faction, "phoenix", hp, speed, hold, hp, 0, x, y, 0m, Map.Id);

    /// <summary>Entra al mundo por el mismo camino que el juego: un JoinCmd al
    /// inbox y un tick que lo procesa.</summary>
    public FakePort Enter(long accountId, uint laserDamage = 100, uint shield = 1_000,
        Dictionary<long, uint>? cargo = null, PlayerData? data = null)
    {
        var who = data ?? Pilot(accountId);
        var port = new FakePort(accountId);
        W.Post(new JoinCmd(port, who, 1, laserDamage, shield, cargo ?? []));
        Tick();
        _naves[accountId] = (who.PosX, who.PosY, who.BaseSpeed);
        return port;
    }

    /// <summary>El primer NPC vivo. Las pruebas de combate trabajan sobre el.</summary>
    public Entity FirstNpc() => W.LiveNpcs.Values.First();

    /// <summary>Mueve al jugador por el camino real —MoveIntent + ticks— y tickea
    /// EXACTAMENTE lo que la nave tarda en llegar a su velocidad. Tickear "de
    /// sobra" no es gratis: el mundo sigue vivo mientras tanto.</summary>
    public void MoveTo(FakePort port, double x, double y)
    {
        var (fromX, fromY, speed) = _naves[port.AccountId];
        var targetX = Math.Clamp(x, 0, Map.BoundsX);
        var targetY = Math.Clamp(y, 0, Map.BoundsY);
        W.Post(new MoveIntentCmd(port, NextSeq(port.AccountId),
            (uint)Math.Round(targetX), (uint)Math.Round(targetY)));
        var dist = Math.Sqrt(Math.Pow(targetX - fromX, 2) + Math.Pow(targetY - fromY, 2));
        var step = speed * Dt;
        Tick((int)Math.Ceiling(dist / step) + 1);
        _naves[port.AccountId] = (targetX, targetY, speed);
    }

    /// <summary>Deja al jugador a `separacion` unidades de la entidad.</summary>
    public void MoveNear(FakePort port, Entity target, double gap = 100) =>
        MoveTo(port, target.X + gap, target.Y);

    private ulong NextSeq(long accountId) =>
        _seq[accountId] = _seq.GetValueOrDefault(accountId) + 1;

    /// <summary>Donde cree la prueba que esta la nave (para armar el siguiente movimiento).</summary>
    public (double X, double Y) Ship(long accountId) =>
        (_naves[accountId].X, _naves[accountId].Y);
}

public static class Wait
{
    /// <summary>El mundo persiste FUERA del hilo del tick (`Task.Run`), asi que
    /// afirmar sobre la BD justo despues del tick es una carrera perdida. Esto
    /// espera al efecto, con tope: si no ocurre, la prueba falla por lo que es
    /// —no ocurrio— y no por un `Sleep` mal calibrado.</summary>
    public static void A(Func<bool> condition, string what, int timeoutMs = 3_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException($"nunca ocurrio: {what}");
    }
}

/// <summary>El reloj de pared, clavado. Lo unico que lo usa es la marca de
/// tiempo del chat, y una prueba no puede depender de que hora es.</summary>
public sealed class FixedClock : IClock
{
    public long UnixMs => 1_756_000_000_000;
}
