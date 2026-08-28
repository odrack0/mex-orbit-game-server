// El conjunto de mapas vivos, y el unico sitio que sabe de mas de uno.
//
// El server nacio de UN mapa: un World, un bucle de tick, y la conexion atada a
// el. El salto de sector obliga a tener varios, y hay dos formas de hacerlo mal:
//
//   · Un World por mapa con su propio temporizador. Serian 29 relojes para 28
//     mapas vacios — tirar el tiempo de CPU en simular la nada.
//   · Crear los 29 al arrancar. Serian 29 consultas a BD y 29 poblaciones de NPC
//     antes de que exista un solo jugador.
//
// Aqui los mapas se crean **cuando alguien entra** y se tickean desde UN solo
// bucle, saltandose los vacios. Un mapa sin jugadores no necesita simularse: sus
// NPC no vagabundean para nadie.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed class Universe(IMapCatalog maps, IGameCatalog catalogo,
    IPlayerRepository jugadores, ISessionRepository sesiones, IEconomyRepository economia,
    IServerCodec codec, IClock clock, RangosDeRelevancia rangos, ILoggerFactory logs,
    int tickMs, int pingIntervalSeconds, int pingMissesToDrop, bool npcCombatEnabled)
{
    private readonly Dictionary<string, World> _mundos = new();
    private readonly ILogger _log = logs.CreateLogger<Universe>();

    /// <summary>El mapa de entrada. Es el unico que se crea al arrancar, porque es
    /// el unico al que se llega sin haber estado antes en otro.</summary>
    public World Inicial()
    {
        var mapa = maps.LoadStarterMap();
        return Obtener(mapa.Code)
            ?? throw new InvalidOperationException($"el mapa inicial {mapa.Code} no carga");
    }

    /// <summary>El mundo de un mapa, creandolo si es la primera vez que alguien va.</summary>
    public World? Obtener(string code)
    {
        if (_mundos.TryGetValue(code, out var ya)) return ya;

        var mapa = maps.LoadMap(code);
        if (mapa == null)
        {
            _log.LogWarning("alguien pidio el mapa {code} y no existe en BD", code);
            return null;
        }
        var mundo = new World(mapa, catalogo.LoadNpcSpawns(mapa.Id),
            catalogo.LoadZoneBias(mapa.ZoneTier), catalogo.LoadRefineRecipe(),
            catalogo.LoadNpcPrices(), maps.LoadPortals(mapa.Id),
            jugadores, sesiones, economia, codec, clock, rangos, logs.CreateLogger<World>(),
            tickMs, pingIntervalSeconds, pingMissesToDrop, npcCombatEnabled);
        mundo.SpawnNpcs();
        mundo.Saltar += Saltar;
        _mundos[code] = mundo;
        _log.LogInformation("mapa {code} levantado ({x}x{y})", mapa.Code, mapa.BoundsX, mapa.BoundsY);
        return mundo;
    }

    /// <summary>UN bucle para todos. El tick de un mapa jamas tumba a los demas:
    /// cada World ya se protege por dentro, y aqui se protege ademas el recorrido.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var dt = tickMs / 1000.0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                foreach (var mundo in _mundos.Values.ToList())
                    if (!mundo.Ocioso) mundo.Paso(dt);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "excepcion recorriendo los mapas");
            }
        }
    }

    /// <summary>El salto. **Negocia siempre**, aunque el destino lo sirva este
    /// mismo proceso.
    ///
    /// Podria haber un atajo —si el mapa es mio, muevo al jugador en memoria— y
    /// seria mas rapido. Pero entonces el camino del handoff no se ejecutaria
    /// nunca hasta el dia que se parta la carga, que es el peor momento posible
    /// para descubrir que no funciona. Sin atajo, partir manana es cambiar filas
    /// de `map_server`.
    ///
    /// El orden es el que es por una razon: primero se comprueba que el destino
    /// existe y TIENE servidor, luego se avisa al cliente de a donde ir, y solo
    /// entonces se suelta. Al reves, un destino sin servidor dejaria al jugador
    /// fuera de todo mapa.</summary>
    private void Saltar(World origen, long accountId, PortalInfo portal)
    {
        var mapa = maps.LoadMap(portal.TargetMapCode);
        if (mapa == null)
        {
            _log.LogWarning("salto a {code}: el mapa no existe", portal.TargetMapCode);
            return;
        }
        var servidor = maps.LoadMapServer(mapa.Id);
        if (servidor == null)
        {
            _log.LogWarning("salto a {code}: el mapa no tiene servidor asignado", mapa.Code);
            return;
        }
        origen.AvisarHandoff(accountId, mapa.Code, servidor);
        origen.SoltarPorSalto(accountId, mapa.Id, portal.TargetX, portal.TargetY);
        _log.LogInformation("cuenta {id} salta de {a} a {b} en {host}:{port}",
            accountId, origen.Mapa.Code, mapa.Code, servidor.Host, servidor.Port);
    }

    /// <summary>El mundo donde esta un jugador segun la BD. Es lo que hace que se
    /// vuelva a entrar DONDE SE DEJO el juego y no siempre en el mapa inicial —
    /// un fallo que existia desde antes del salto y que solo se nota cuando hay
    /// mas de un mapa al que volver.</summary>
    public World? DondeEsta(long mapId)
    {
        var mapa = maps.LoadMapPorId(mapId);
        return mapa == null ? null : Obtener(mapa.Code);
    }
}
