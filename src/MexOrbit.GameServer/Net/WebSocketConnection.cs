// Una conexion WebSocket, y NADA MAS que eso: bytes que entran, bytes que salen.
//
// Lo que antes vivia aqui y ya no: la verificacion del ticket, la apertura de
// sesion, la eleccion del mapa y el armado de comandos. Todo eso eran decisiones
// de juego escritas dentro del bucle de un socket. Ahora esta clase no sabe que
// es un ticket ni que es un mapa — solo sabe leer un frame, pedirle al handshake
// o al lector que lo traduzcan, y empujar el resultado al mundo.
using System.Net.WebSockets;
using System.Threading.Channels;
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Protocol;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Net;

public sealed class WebSocketConnection(WebSocket socket, Handshake handshake, IServerCodec codec)
    : IClientPort
{
    private const int MaxFrame = 64 * 1024;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();

    private World? _world;

    public long AccountId { get; private set; }

    public void Send(byte[] frame) => _outbox.Writer.TryWrite(frame);

    public void CloseSocket() => _cts.Cancel();

    public async Task RunAsync()
    {
        var sending = Task.Run(SendLoopAsync);
        try
        {
            // ---- handshake: el primer frame es Hello (entrar) o Resume (volver) ----
            var first = await ReceiveFrameAsync();
            if (first is null) return;

            var link = DoHandshake(first);
            if (link is null) return;
            _world = link.World;
            AccountId = link.AccountId;

            await LoopAsync();
        }
        catch (OperationCanceledException) { /* cierre pedido por el mundo */ }
        catch (WebSocketException) { /* socket caido: el mundo hara el drop */ }
        finally
        {
            // DROPPED (no LOGOUT): una caida abre la ventana de gracia; solo el
            // LogoutRequest explicito saca la nave del mundo
            _world?.Post(new LeaveCmd(this, "DROPPED"));
            _outbox.Writer.TryComplete();
            try { await sending; } catch { /* ya cerrando */ }
            socket.Dispose();
        }
    }

    private HandshakeResult? DoHandshake(byte[] first)
    {
        try
        {
            if (ClientFrames.IsResume(first))
            {
                var (version, token) = ClientFrames.ReadResume(first);
                return handshake.Resume(this, token, version);
            }
            if (ClientFrames.IsHello(first))
            {
                var (version, ticket) = ClientFrames.ReadHello(first);
                return handshake.Enter(this, ticket, version);
            }
            Send(new Failed(0, Domain.ErrorCode.Invalid, "se esperaba Hello o Resume"));
            return null;
        }
        catch (ProtocolViolationException e)
        {
            Send(new Failed(0, Domain.ErrorCode.Invalid, e.Message));
            return null;
        }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var frame = await ReceiveFrameAsync();
            if (frame is null) break;
            Dispatch(frame);
        }
    }

    private void Dispatch(byte[] frame)
    {
        try
        {
            var cmd = ClientFrames.Read(this, frame);
            // mensaje desconocido o fuera de lugar: se ignora (jamas rompe la sesion)
            if (cmd is null) return;
            _world?.Post(cmd);
            // el logout ademas cuelga: el mundo no cierra sockets ajenos
            if (ClientFrames.IsLogout(frame)) _cts.Cancel();
        }
        catch (ProtocolViolationException e)
        {
            // violacion del contrato = mensaje descartado con aviso; el rate limiting
            // por tipo declarado en el esquema llega con el generador de limiters (I5)
            Send(new Failed(0, Domain.ErrorCode.Invalid, e.Message));
        }
    }

    private void Send(ServerEvent serverEvent) => Send(codec.Encode(serverEvent));

    private async Task<byte[]?> ReceiveFrameAsync()
    {
        var buffer = new byte[MaxFrame];
        var total = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(total), _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            total += result.Count;
            if (result.EndOfMessage) return buffer[..total];
            if (total >= MaxFrame) throw new WebSocketException("frame > 64 KB");
        }
    }

    private async Task SendLoopAsync()
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync())
        {
            if (socket.State != WebSocketState.Open) break;
            try { await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); }
            catch (WebSocketException) { break; }
        }
    }
}
