// mex-orbit-game-server — el host: configuracion, cableado y el endpoint del socket.
//
// Este archivo es la RAIZ DE COMPOSICION y es el unico sitio del server donde se
// nombra a la vez un adaptador concreto (MySQL, Ed25519, el codec del protocolo) y
// lo que lo usa. Todo lo de dentro conoce interfaces; aqui se decide cuales.
//
// Mapas bajo demanda con un solo tick de 80 ms, handshake con ticket Ed25519 de la
// api, sesion unica. Transporte dev: ws:// en 5200 (TLS lo aporta la
// infraestructura en prod: wss://).
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using MexOrbit.GameServer.Infrastructure;
using MexOrbit.GameServer.Net;
using MexOrbit.GameServer.Protocol;

var builder = WebApplication.CreateBuilder(args);
var conn = builder.Configuration.GetConnectionString("Default")
           ?? throw new InvalidOperationException("falta ConnectionStrings:Default");
var tickMs = builder.Configuration.GetValue("Game:TickMs", 80);
var protocolVersion = builder.Configuration.GetValue("Game:ProtocolVersion", 1);
var pingInterval = builder.Configuration.GetValue("Game:PingIntervalSeconds", 10);
var pingMisses = builder.Configuration.GetValue("Game:PingMissesToDrop", 3);
var pubKeyPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    builder.Configuration.GetValue("Game:ApiPublicKeyPath", "keys/ed25519.pub")!));

var app = builder.Build();
var logs = app.Services.GetRequiredService<ILoggerFactory>();

// ─── los adaptadores concretos ──────────────────────────────────────────────
var maps = new MapCatalog(conn);
var catalog = new GameCatalog(conn);
var settings = new ServerSettings(conn);
var players = new PlayerRepository(conn);
var sessions = new SessionRepository(conn);
var economy = new EconomyRepository(conn);
var codec = new ServerCodec();
var clock = new SystemClock();
var verifier = new Ed25519TicketVerifier(pubKeyPath);

// diales de JUEGO, no de despliegue: viven en server_setting con su auditoria
var npcCombat = settings.LoadBoolSetting("npc_combat_enabled", true);
// A que distancia el cliente empieza —y deja— de saber que algo existe. La spec
// del protocolo fija los valores iniciales y dice que son calibrables en BD; si
// las filas faltan se usan los de respaldo y el server arranca igual.
var ranges = new RelevanceRanges(
    settings.LoadIntSetting("render_range_entities", (int)RelevanceRanges.Fallback.Entities),
    settings.LoadIntSetting("render_range_objects", (int)RelevanceRanges.Fallback.Objects),
    (byte)settings.LoadIntSetting("render_range_hysteresis_pct",
        RelevanceRanges.Fallback.HysteresisPct));

// ─── la simulacion ──────────────────────────────────────────────────────────
// Los mapas se levantan cuando alguien entra, no al arrancar: 29 mapas serian
// 29 consultas y 29 poblaciones de NPC antes de que exista un solo jugador.
var universe = new Universe(maps, catalog, players, sessions, economy, codec, clock, ranges,
    logs, tickMs, pingInterval, pingMisses, npcCombat);
var map = universe.Starter().Map;
var handshake = new Handshake(universe, players, sessions, verifier, codec, clock,
    protocolVersion, tickMs);

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(() => universe.RunAsync(lifetime.ApplicationStopping));

app.UseWebSockets();
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await new WebSocketConnection(socket, handshake, codec).RunAsync();
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", map = map.Code }));

app.Logger.LogInformation("game server listo: entrada {code} ({x}x{y}), tick {tick} ms, clave publica {key}",
    map.Code, map.BoundsX, map.BoundsY, tickMs, pubKeyPath);
// se anuncian como el resto de diales de BD: cambiarlos pide reiniciar, y el log
// de arranque es donde se comprueba con que numeros esta corriendo de verdad
app.Logger.LogInformation("relevancia por rango: entidades {e} u · cajas {c} u · histeresis {h}%",
    ranges.Entities, ranges.Objects, ranges.HysteresisPct);
app.Run();
