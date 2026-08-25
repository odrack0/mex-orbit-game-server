// mex-orbit-game-server — el servidor de simulacion, minimo del vertical slice (E2/I3).
// Un mapa, tick fijo de 80 ms, handshake con ticket Ed25519 de la api, sesion unica.
// Transporte dev: ws:// en 5200 (TLS lo aporta la infraestructura en prod: wss://).
using MexOrbit.GameServer.Data;
using MexOrbit.GameServer.Game;
using MexOrbit.GameServer.Net;

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
var log = app.Services.GetRequiredService<ILoggerFactory>();

var repo = new Repo(conn);
var mapa = repo.LoadStarterMap();
var spawns = repo.LoadNpcSpawns(mapa.Id);
var bias = repo.LoadZoneBias(mapa.ZoneTier);
var receta = repo.LoadRefineRecipe();
var precios = repo.LoadNpcPrices();
var portales = repo.LoadPortals(mapa.Id);
var world = new World(mapa, spawns, bias, receta, precios, portales, repo, log.CreateLogger<World>(),
    tickMs, pingInterval, pingMisses);
world.SpawnNpcs();

var verifier = new TicketVerifier(pubKeyPath);
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(() => world.RunAsync(lifetime.ApplicationStopping));

app.UseWebSockets();
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var conexion = new ClientConnection(socket, world, repo, verifier, protocolVersion,
        log.CreateLogger<ClientConnection>());
    await conexion.RunAsync();
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", map = mapa.Code }));

app.Logger.LogInformation("game server listo: mapa {code} ({x}x{y}), tick {tick} ms, clave publica {key}",
    mapa.Code, mapa.BoundsX, mapa.BoundsY, tickMs, pubKeyPath);
app.Run();
