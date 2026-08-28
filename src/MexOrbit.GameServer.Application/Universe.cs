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

public sealed class Universe(IMapCatalog maps, IGameCatalog catalog,
    IPlayerRepository players, ISessionRepository sessions, IEconomyRepository economy,
    IServerCodec codec, IClock clock, RelevanceRanges ranges, ILoggerFactory logs,
    int tickMs, int pingIntervalSeconds, int pingMissesToDrop, bool npcCombatEnabled)
{
    private readonly Dictionary<string, World> _mundos = new();
    private readonly ILogger _log = logs.CreateLogger<Universe>();

    /// <summary>El mapa de entrada. Es el unico que se crea al arrancar, porque es
    /// el unico al que se llega sin haber estado antes en otro.</summary>
    public World Starter()
    {
        var map = maps.LoadStarterMap();
        return Get(map.Code)
            ?? throw new InvalidOperationException($"el mapa inicial {map.Code} no carga");
    }

    /// <summary>El mundo de un mapa, creandolo si es la primera vez que alguien va.</summary>
    public World? Get(string code)
    {
        if (_mundos.TryGetValue(code, out var ya)) return ya;

        var map = maps.LoadMap(code);
        if (map == null)
        {
            _log.LogWarning("alguien pidio el mapa {code} y no existe en BD", code);
            return null;
        }
        var world = new World(map, catalog.LoadNpcSpawns(map.Id),
            catalog.LoadZoneBias(map.ZoneTier), catalog.LoadRefineRecipe(),
            catalog.LoadNpcPrices(), maps.LoadPortals(map.Id),
            players, sessions, economy, codec, clock, ranges, logs.CreateLogger<World>(),
            tickMs, pingIntervalSeconds, pingMissesToDrop, npcCombatEnabled);
        world.SpawnNpcs();
        world.Jump += Jump;
        _mundos[code] = world;
        _log.LogInformation("mapa {code} levantado ({x}x{y})", map.Code, map.BoundsX, map.BoundsY);
        return world;
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
                foreach (var world in _mundos.Values.ToList())
                    if (!world.Idle) world.Paso(dt);
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
    private void Jump(World origen, long accountId, PortalInfo portal)
    {
        var map = maps.LoadMap(portal.TargetMapCode);
        if (map == null)
        {
            _log.LogWarning("salto a {code}: el mapa no existe", portal.TargetMapCode);
            return;
        }
        var server = maps.LoadMapServer(map.Id);
        if (server == null)
        {
            _log.LogWarning("salto a {code}: el mapa no tiene servidor asignado", map.Code);
            return;
        }
        origen.NotifyHandoff(accountId, map.Code, server);
        origen.ReleaseForJump(accountId, map.Id, portal.TargetX, portal.TargetY);
        _log.LogInformation("cuenta {id} salta de {a} a {b} en {host}:{port}",
            accountId, origen.Map.Code, map.Code, server.Host, server.Port);
    }

    /// <summary>El mundo donde esta un jugador segun la BD. Es lo que hace que se
    /// vuelva a entrar DONDE SE DEJO el juego y no siempre en el mapa inicial —
    /// un fallo que existia desde antes del salto y que solo se nota cuando hay
    /// mas de un mapa al que volver.</summary>
    public World? WhereIs(long mapId)
    {
        var map = maps.LoadMapById(mapId);
        return map == null ? null : Get(map.Code);
    }
}
