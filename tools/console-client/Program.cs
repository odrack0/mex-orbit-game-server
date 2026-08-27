// Cliente de consola del slice (el observable de E2/I3):
// login HTTP contra la api -> ws al game server -> Hello -> volar por el mapa.
// Sale con 0 si recibio Welcome, EnterMap, spawns y ecos de movimiento que avanzan.
// Uso: dotnet run -- [usuario] [password]     (default: testbot / dev1234)
//      API=https://astrion.turname.mx/api dotnet run -- cuenta clave
//
// La URL del game server NO se configura: se usa el `game_host` que devuelve el
// login, igual que hace el cliente de verdad. Asi esta prueba comprueba tambien
// que ese campo esta bien puesto en produccion, que es justo lo que falla si se
// despliega sin tocarlo.
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using MexOrbit.Protocol;

var usuario = args.Length > 0 ? args[0] : "testbot";
var password = args.Length > 1 ? args[1] : "dev1234";

// ---- 1. login HTTP ----
// Sin BaseAddress, y a proposito. Es la trampa clasica de HttpClient: una ruta
// relativa que empieza por "/" REEMPLAZA el camino de la base, asi que con una
// base de "https://.../api" el "/api" desaparecia y todo salia 404. En dev nunca
// se vio porque alli la base no tiene camino. Concatenar es feo y no miente.
var apiBase = (Environment.GetEnvironmentVariable("API") ?? "http://127.0.0.1:5100").TrimEnd('/');
using var http = new HttpClient();
Console.WriteLine($"api: {apiBase}");
var loginResp = await http.PostAsJsonAsync($"{apiBase}/v1/auth/login", new { username = usuario, password });
if (!loginResp.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"FALLO login: HTTP {(int)loginResp.StatusCode}");
    return 1;
}
var login = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
var ticket = login.GetProperty("game_ticket").GetString()!;
var gameHost = login.GetProperty("game_host").GetString()!;
Console.WriteLine($"login OK: {login.GetProperty("pilot_name")} (cuenta {login.GetProperty("account_id")})");

// ---- 2. conectar y Hello ----
using var ws = new ClientWebSocket();
Console.WriteLine($"game_host: {gameHost}");
await ws.ConnectAsync(new Uri(gameHost), CancellationToken.None);
await Enviar(new Hello { ProtocolVersion = 1, GameTicket = ticket }.Encode());
Console.WriteLine("socket abierto, Hello enviado");

// receptor dedicado: cancelar un ReceiveAsync aborta el socket, asi que el
// timeout se aplica sobre el canal, nunca sobre el socket
var entrada = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
_ = Task.Run(async () =>
{
    var buffer = new byte[65536];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) break;
            entrada.Writer.TryWrite(buffer[..r.Count]);
        }
    }
    catch (WebSocketException) { /* cierre */ }
    entrada.Writer.TryComplete();
});

// ---- 3. escuchar y volar ----
bool welcome = false, enterMap = false;
var spawns = 0;
ulong heroId = 0, mapaX = 0, mapaY = 0;
var ecosDelHeroe = new List<(ulong X, ulong Y, ulong Tx, ulong Ty)>();
var rng = new Random();
ulong seq = 0;
var fin = DateTime.UtcNow.AddSeconds(12);
var proximoIntento = DateTime.UtcNow.AddSeconds(1);

while (DateTime.UtcNow < fin)
{
    var frame = await RecibirAsync(TimeSpan.FromMilliseconds(300));
    if (frame is not null)
    {
        switch (MsgId(frame))
        {
            case Welcome.MsgId:
                var w = Welcome.Decode(frame);
                welcome = true;
                heroId = w.AccountId;
                Console.WriteLine($"Welcome: cuenta {w.AccountId}, tick {w.TickRate} Hz, reconnect_token {w.ReconnectToken[..8]}...");
                break;
            case EnterMap.MsgId:
                var em = EnterMap.Decode(frame);
                enterMap = true;
                (mapaX, mapaY) = (em.LimitsX, em.LimitsY);
                Console.WriteLine($"EnterMap: {em.MapCode} ({em.LimitsX}x{em.LimitsY}), riesgo de carga {em.CargoRiskPct}%");
                break;
            case EntitySpawn.MsgId:
                var sp = EntitySpawn.Decode(frame);
                spawns++;
                if (spawns <= 3 || sp.EntityId == heroId)
                    Console.WriteLine($"  spawn: {sp.Kind} {sp.TypeId} '{sp.Name}' en ({sp.X},{sp.Y})");
                break;
            case EntityMove.MsgId:
                var mv = EntityMove.Decode(frame);
                if (mv.EntityId == heroId)
                {
                    ecosDelHeroe.Add((mv.X, mv.Y, mv.TargetX, mv.TargetY));
                    Console.WriteLine($"  eco propio: pos ({mv.X},{mv.Y}) -> target ({mv.TargetX},{mv.TargetY})");
                }
                break;
            case Ping.MsgId:
                await Enviar(new Pong { Nonce = Ping.Decode(frame).Nonce }.Encode());
                break;
            case ErrorReply.MsgId:
                var err = ErrorReply.Decode(frame);
                Console.Error.WriteLine($"ErrorReply: {err.Code} {err.Detail}");
                break;
            case SessionReplaced.MsgId:
                Console.WriteLine("SessionReplaced: otra conexion tomo la cuenta; cerrando limpio");
                return 0;
        }
    }
    if (enterMap && DateTime.UtcNow >= proximoIntento && seq < 3)
    {
        var intent = new MoveIntent
        {
            Seq = ++seq,
            TargetX = (ulong)rng.Next(1000, (int)mapaX - 1000),
            TargetY = (ulong)rng.Next(1000, (int)mapaY - 1000),
        };
        await Enviar(intent.Encode());
        Console.WriteLine($"MoveIntent #{intent.Seq} -> ({intent.TargetX},{intent.TargetY})");
        proximoIntento = DateTime.UtcNow.AddSeconds(3);
    }
}

await Enviar(new LogoutRequest().Encode());

// ---- 4. veredicto ----
var volo = ecosDelHeroe.Count >= 3;
Console.WriteLine($"\nresumen: welcome={welcome} enterMap={enterMap} spawns={spawns} ecosPropios={ecosDelHeroe.Count}");
if (welcome && enterMap && spawns >= 16 && volo)
{
    Console.WriteLine("CLIENTE-CONSOLA OK — el heroe volo por el 1-1 con eco autoritativo del server");
    return 0;
}
Console.Error.WriteLine("FALLO: faltaron mensajes del flujo");
return 1;

async Task Enviar(byte[] datos)
{
    try { await ws.SendAsync(datos, WebSocketMessageType.Binary, true, CancellationToken.None); }
    catch (WebSocketException) { /* socket cerrado por el server (p. ej. expulsion): tolerable */ }
}

async Task<byte[]?> RecibirAsync(TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    try { return await entrada.Reader.ReadAsync(cts.Token); }
    catch (OperationCanceledException) { return null; }
    catch (System.Threading.Channels.ChannelClosedException) { return null; }
}

static int MsgId(byte[] frame)
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
