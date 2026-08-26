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
using MexOrbit.GameServer.Data;

namespace MexOrbit.GameServer.Game;

public sealed class Universe(Repo repo, ILoggerFactory logs, int tickMs,
    int pingIntervalSeconds, int pingMissesToDrop, bool npcCombatEnabled)
{
    private readonly Dictionary<string, World> _mundos = new();
    private readonly ILogger _log = logs.CreateLogger<Universe>();

    /// <summary>El mapa de entrada. Es el unico que se crea al arrancar, porque es
    /// el unico al que se llega sin haber estado antes en otro.</summary>
    public World Inicial()
    {
        var mapa = repo.LoadStarterMap();
        return Obtener(mapa.Code)
            ?? throw new InvalidOperationException($"el mapa inicial {mapa.Code} no carga");
    }

    /// <summary>El mundo de un mapa, creandolo si es la primera vez que alguien va.</summary>
    public World? Obtener(string code)
    {
        if (_mundos.TryGetValue(code, out var ya)) return ya;

        var mapa = repo.LoadMap(code);
        if (mapa == null)
        {
            _log.LogWarning("alguien pidio el mapa {code} y no existe en BD", code);
            return null;
        }
        var mundo = new World(mapa, repo.LoadNpcSpawns(mapa.Id), repo.LoadZoneBias(mapa.ZoneTier),
            repo.LoadRefineRecipe(), repo.LoadNpcPrices(), repo.LoadPortals(mapa.Id),
            repo, logs.CreateLogger<World>(), tickMs, pingIntervalSeconds, pingMissesToDrop,
            npcCombatEnabled);
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

    /// <summary>El traslado. Es lo unico que necesita conocer los dos mapas a la vez,
    /// y por eso vive aqui y no en World.
    ///
    /// El orden importa: primero se comprueba que el destino EXISTE y luego se saca
    /// al jugador del origen. Al reves, un mapa destino roto dejaria al jugador en
    /// ninguna parte — sin mundo al que volver y sin mundo al que llegar.</summary>
    private void Saltar(World origen, long accountId, PortalInfo portal)
    {
        var destino = Obtener(portal.TargetMapCode);
        if (destino == null || ReferenceEquals(destino, origen)) return;

        var snap = origen.SacarParaSalto(accountId);
        if (snap == null) return;

        // El destino se ejecuta en el MISMO hilo del bucle (los dos mundos se
        // tickean aqui), asi que la entrega es directa y no hay carrera: el
        // jugador nunca esta en los dos mapas a la vez ni en ninguno.
        destino.MeterDesdeSalto(snap, portal.TargetX, portal.TargetY);
        Mudanza?.Invoke(snap.Port, destino);
        _log.LogInformation("cuenta {id} salto de {a} a {b}", accountId,
            origen.Mapa.Code, destino.Mapa.Code);
    }

    /// <summary>Avisa a la conexion de que su mundo cambio: sus proximos comandos
    /// tienen que ir al mapa nuevo, no al que acaba de dejar.</summary>
    public event Action<IClientPort, World>? Mudanza;
}
