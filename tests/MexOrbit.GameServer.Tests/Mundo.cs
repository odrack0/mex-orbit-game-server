// El armador: un World listo para jugar en tres lineas, con los mismos diales
// que corren en produccion (tick de 80 ms) y catalogos minimos pero reales.
//
// El heartbeat viene APAGADO por defecto (intervalo de una hora). No es pereza:
// con los 10 s reales, cualquier prueba que tickee mas de 30 s vería su jugador
// expulsado por no contestar unos Pong que la prueba no esta ejercitando. Quien
// caracteriza el heartbeat lo enciende con `ConPing`, y ahi si son los diales
// de verdad.
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.GameServer.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace MexOrbit.GameServer.Tests;

public sealed class Mundo
{
    public const int TickMs = 80;
    public const double Dt = TickMs / 1000.0;
    /// <summary>Ticks que caben en un segundo con el tick de 80 ms.</summary>
    public const int PorSegundo = 1000 / TickMs;

    public BdFalsa Bd { get; } = new();
    public MapInfo Mapa { get; private set; } =
        new(1, "1-1", "Sector 1-1", 20800, 12800, 10_000, 6_000, 1_500, "core");

    private readonly List<NpcSpawnInfo> _spawns = [];
    private List<MaterialBias> _bias = [new(10, "asterium", 1m)];
    private RefineRecipe? _receta;
    private List<NpcPrice> _precios = [new(10, "asterium", 5m)];
    private readonly List<PortalInfo> _portales = [];
    private readonly Dictionary<long, (double X, double Y, uint Velocidad)> _naves = [];
    private readonly Dictionary<long, ulong> _seq = [];
    private bool _combateNpc = true;
    private RangosDeRelevancia _rangos = RangosDeRelevancia.PorDefecto;
    private int _pingSegundos = 3_600;
    private int _pingFallos = 3;
    private World? _world;

    public World W => _world ?? throw new InvalidOperationException("llama a Construir() primero");

    // ─── armado ─────────────────────────────────────────────────────────────

    public Mundo ConMapa(uint boundsX, uint boundsY, uint stationX, uint stationY, uint secureRange)
    {
        Mapa = Mapa with
        {
            BoundsX = boundsX, BoundsY = boundsY,
            StationX = stationX, StationY = stationY, SecureRange = secureRange,
        };
        return this;
    }

    /// <summary>Un bicho de laboratorio: quieto (velocidad 0) y ciego (aggro 0),
    /// asi que se queda donde el sorteo lo puso y no persigue a nadie. Es el
    /// unico modo de tener un blanco fijo sin tocar el estado privado del mundo.</summary>
    public Mundo ConManiqui(uint hp = 1_000, uint escudo = 0, uint dropMin = 30, uint dropMax = 30,
        uint recompensa = 100, ushort cuantos = 1) =>
        ConNpc(new NpcSpawnInfo(1, "vex", "Vex", hp, escudo, 0, 50, false, 0, 0, 30, cuantos,
            0, 0, recompensa, dropMin, dropMax));

    public Mundo ConNpc(NpcSpawnInfo spawn) { _spawns.Add(spawn); return this; }
    public Mundo ConPortal(PortalInfo portal) { _portales.Add(portal); return this; }
    public Mundo ConReceta(RefineRecipe receta) { _receta = receta; return this; }
    public Mundo ConBias(params MaterialBias[] bias) { _bias = [.. bias]; return this; }
    public Mundo ConPrecios(params NpcPrice[] precios) { _precios = [.. precios]; return this; }
    public Mundo SinCombateNpc() { _combateNpc = false; return this; }

    /// <summary>Los rangos de relevancia. Por defecto los de la spec (2000 / 1250
    /// con 10% de histeresis); una prueba que quiera ver el mapa entero pone
    /// `SinRelevancia()`.</summary>
    public Mundo ConRangos(double entidades, double objetos, byte histeresisPct = 10)
    {
        _rangos = new RangosDeRelevancia(entidades, objetos, histeresisPct);
        return this;
    }

    /// <summary>Rango practicamente infinito: para las pruebas que caracterizan
    /// OTRA cosa y no quieren que la visibilidad les mueva el suelo.</summary>
    public Mundo SinRelevancia() => ConRangos(1_000_000, 1_000_000, 0);

    /// <summary>Enciende el heartbeat con sus diales reales.</summary>
    public Mundo ConPing(int segundos = 10, int fallos = 3)
    {
        _pingSegundos = segundos;
        _pingFallos = fallos;
        return this;
    }

    public Mundo Construir()
    {
        // El codec es el DE VERDAD: las pruebas afirman sobre los mismos bytes que
        // recibiria el cliente de Godot, no sobre un doble complaciente.
        _world = new World(Mapa, _spawns, _bias, _receta, _precios, _portales,
            Bd, Bd, Bd, new ServerCodec(), new RelojFijo(), _rangos, NullLogger<World>.Instance,
            TickMs, _pingSegundos, _pingFallos, _combateNpc);
        _world.SpawnNpcs();
        return this;
    }

    // ─── el reloj ───────────────────────────────────────────────────────────

    public Mundo Tick(int veces = 1)
    {
        for (var i = 0; i < veces; i++) W.Paso(Dt);
        return this;
    }

    /// <summary>Ticks exactos para esos segundos. Con `PorSegundo` (division
    /// entera: 12) se perdia medio tick por segundo, y a los 150 s de una caja
    /// eso son 4 s de menos: la prueba miraba antes de que el plazo venciera.</summary>
    public Mundo Segundos(double s) => Tick((int)Math.Ceiling(s * 1000 / TickMs));

    /// <summary>Tickea hasta que se cumpla la condicion. Para lo que depende del
    /// sorteo (un NPC que arranca a vagabundear) y no de un plazo fijo.</summary>
    public Mundo TickHasta(Func<bool> condicion, int tope = 200, string que = "la condicion")
    {
        for (var i = 0; i < tope; i++)
        {
            if (condicion()) return this;
            Tick();
        }
        if (!condicion()) throw new InvalidOperationException($"nunca se cumplio: {que}");
        return this;
    }

    // ─── jugadores ──────────────────────────────────────────────────────────

    public PlayerData Piloto(long accountId, string nombre = "Piloto", byte faccion = 1,
        uint hp = 4_000, ushort velocidad = 320, uint bodega = 300,
        uint x = 10_000, uint y = 6_000) =>
        new(accountId, nombre, faccion, "phoenix", hp, velocidad, bodega, hp, 0, x, y, 0m, Mapa.Id);

    /// <summary>Entra al mundo por el mismo camino que el juego: un JoinCmd al
    /// inbox y un tick que lo procesa.</summary>
    public PuertoFalso Entrar(long accountId, uint danioLaser = 100, uint escudo = 1_000,
        Dictionary<long, uint>? carga = null, PlayerData? datos = null)
    {
        var quien = datos ?? Piloto(accountId);
        var puerto = new PuertoFalso(accountId);
        W.Post(new JoinCmd(puerto, quien, 1, danioLaser, escudo, carga ?? []));
        Tick();
        _naves[accountId] = (quien.PosX, quien.PosY, quien.BaseSpeed);
        return puerto;
    }

    /// <summary>El primer NPC vivo. Las pruebas de combate trabajan sobre el.</summary>
    public Entity PrimerNpc() => W.NpcsVivos.Values.First();

    /// <summary>Mueve al jugador por el camino real —MoveIntent + ticks— y tickea
    /// EXACTAMENTE lo que la nave tarda en llegar a su velocidad. Tickear "de
    /// sobra" no es gratis: el mundo sigue vivo mientras tanto.</summary>
    public void MoverA(PuertoFalso puerto, double x, double y)
    {
        var (desdeX, desdeY, velocidad) = _naves[puerto.AccountId];
        var destinoX = Math.Clamp(x, 0, Mapa.BoundsX);
        var destinoY = Math.Clamp(y, 0, Mapa.BoundsY);
        W.Post(new MoveIntentCmd(puerto, SiguienteSeq(puerto.AccountId),
            (uint)Math.Round(destinoX), (uint)Math.Round(destinoY)));
        var dist = Math.Sqrt(Math.Pow(destinoX - desdeX, 2) + Math.Pow(destinoY - desdeY, 2));
        var paso = velocidad * Dt;
        Tick((int)Math.Ceiling(dist / paso) + 1);
        _naves[puerto.AccountId] = (destinoX, destinoY, velocidad);
    }

    /// <summary>Deja al jugador a `separacion` unidades de la entidad.</summary>
    public void Acercar(PuertoFalso puerto, Entity objetivo, double separacion = 100) =>
        MoverA(puerto, objetivo.X + separacion, objetivo.Y);

    private ulong SiguienteSeq(long accountId) =>
        _seq[accountId] = _seq.GetValueOrDefault(accountId) + 1;

    /// <summary>Donde cree la prueba que esta la nave (para armar el siguiente movimiento).</summary>
    public (double X, double Y) Nave(long accountId) =>
        (_naves[accountId].X, _naves[accountId].Y);
}

public static class Espera
{
    /// <summary>El mundo persiste FUERA del hilo del tick (`Task.Run`), asi que
    /// afirmar sobre la BD justo despues del tick es una carrera perdida. Esto
    /// espera al efecto, con tope: si no ocurre, la prueba falla por lo que es
    /// —no ocurrio— y no por un `Sleep` mal calibrado.</summary>
    public static void A(Func<bool> condicion, string que, int msTope = 3_000)
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        while (reloj.ElapsedMilliseconds < msTope)
        {
            if (condicion()) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException($"nunca ocurrio: {que}");
    }
}

/// <summary>El reloj de pared, clavado. Lo unico que lo usa es la marca de
/// tiempo del chat, y una prueba no puede depender de que hora es.</summary>
public sealed class RelojFijo : IClock
{
    public long UnixMs => 1_756_000_000_000;
}
