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
var mapas = new MapCatalog(conn);
var catalogo = new GameCatalog(conn);
var ajustes = new ServerSettings(conn);
var jugadores = new PlayerRepository(conn);
var sesiones = new SessionRepository(conn);
var economia = new EconomyRepository(conn);
var codec = new ServerCodec();
var reloj = new SystemClock();
var verificador = new Ed25519TicketVerifier(pubKeyPath);

// dial de JUEGO, no de despliegue: vive en server_setting con su auditoria
var npcCombat = ajustes.LoadBoolSetting("npc_combat_enabled", true);

// ─── la simulacion ──────────────────────────────────────────────────────────
// Los mapas se levantan cuando alguien entra, no al arrancar: 29 mapas serian
// 29 consultas y 29 poblaciones de NPC antes de que exista un solo jugador.
var universo = new Universe(mapas, catalogo, jugadores, sesiones, economia, codec, reloj, logs,
    tickMs, pingInterval, pingMisses, npcCombat);
var mapa = universo.Inicial().Mapa;
var handshake = new Handshake(universo, jugadores, sesiones, verificador, codec, reloj,
    protocolVersion, tickMs);

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(() => universo.RunAsync(lifetime.ApplicationStopping));

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
app.MapGet("/health", () => Results.Ok(new { status = "ok", map = mapa.Code }));

app.Logger.LogInformation("game server listo: entrada {code} ({x}x{y}), tick {tick} ms, clave publica {key}",
    mapa.Code, mapa.BoundsX, mapa.BoundsY, tickMs, pubKeyPath);
app.Run();
