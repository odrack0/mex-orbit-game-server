// Una conexion WebSocket: handshake (Hello + ticket), loop de recepcion con
// dispatch por msg_id, y cola de envio propia. La conexion NO toca el estado
// del mundo: postea comandos al inbox y transmite lo que el mundo le entregue.
using System.Net.WebSockets;
using System.Threading.Channels;
using MexOrbit.GameServer.Data;
using MexOrbit.GameServer.Game;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Net;

public sealed class ClientConnection(WebSocket socket, World world, Repo repo, TicketVerifier verifier,
    int protocolVersion, ILogger log) : IClientPort
{
    private const int MaxFrame = 64 * 1024;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();

    public long AccountId { get; private set; }

    public void Send(byte[] frame) => _outbox.Writer.TryWrite(frame);

    public void CloseSocket() => _cts.Cancel();

    public async Task RunAsync()
    {
        var envio = Task.Run(SendLoopAsync);
        try
        {
            // ---- handshake: el primer frame DEBE ser Hello valido ----
            var primero = await ReceiveFrameAsync();
            if (primero is null) return;
            if (LeerMsgId(primero) != Hello.MsgId)
            {
                Send(new ErrorReply { Code = ErrorCode.Invalid, Detail = "se esperaba Hello" }.Encode());
                return;
            }
            Hello hello;
            try { hello = Hello.Decode(primero); }
            catch (ProtocolViolationException e)
            {
                Send(new ErrorReply { Code = ErrorCode.Invalid, Detail = e.Message }.Encode());
                return;
            }
            if (hello.ProtocolVersion != (ulong)protocolVersion)
            {
                Send(new ErrorReply { Code = ErrorCode.VersionUnsupported }.Encode());
                return;
            }
            var (accountId, error) = verifier.Verify(hello.GameTicket, protocolVersion);
            if (error is not null)
            {
                Send(new ErrorReply { Code = Enum.Parse<ErrorCode>(Pascal(error), true) }.Encode());
                return;
            }
            var player = repo.LoadPlayer(accountId);
            if (player is null)
            {
                Send(new ErrorReply { Code = ErrorCode.Generic, Detail = "cuenta sin nave" }.Encode());
                return;
            }
            AccountId = accountId;
            var (sessionId, reconnectToken) = repo.OpenSession(accountId);

            Send(new Welcome
            {
                AccountId = (ulong)accountId,
                ReconnectToken = reconnectToken,
                ServerTimeMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TickRate = 1000 / 80u,
            }.Encode());
            world.Post(new JoinCmd(this, player, sessionId));

            // ---- loop principal ----
            while (!_cts.IsCancellationRequested)
            {
                var frame = await ReceiveFrameAsync();
                if (frame is null) break;
                Dispatch(frame);
            }
        }
        catch (OperationCanceledException) { /* cierre pedido por el mundo */ }
        catch (WebSocketException) { /* socket caido: el mundo hara el drop */ }
        finally
        {
            world.Post(new LeaveCmd(this, "LOGOUT"));
            _outbox.Writer.TryComplete();
            try { await envio; } catch { /* ya cerrando */ }
            socket.Dispose();
        }
    }

    private void Dispatch(byte[] frame)
    {
        try
        {
            switch (LeerMsgId(frame))
            {
                case MoveIntent.MsgId: world.Post(new MoveIntentCmd(this, MoveIntent.Decode(frame))); break;
                case Pong.MsgId: world.Post(new PongCmd(this, Pong.Decode(frame).Nonce)); break;
                case LogoutRequest.MsgId: world.Post(new LeaveCmd(this, "LOGOUT")); _cts.Cancel(); break;
                default:
                    // mensaje desconocido o fuera de lugar: se ignora (jamas rompe la sesion)
                    break;
            }
        }
        catch (ProtocolViolationException e)
        {
            // violacion del contrato = mensaje descartado con aviso; el rate limiting
            // por tipo declarado en el esquema llega con el generador de limiters (I5)
            Send(new ErrorReply { Code = ErrorCode.Invalid, Detail = e.Message }.Encode());
        }
    }

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

    private static int LeerMsgId(byte[] frame)
    {
        // varint corto al inicio del frame (los ids del catalogo caben en 2 bytes)
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

    private static string Pascal(string sneke) =>
        string.Concat(sneke.Split('_').Select(p => char.ToUpper(p[0]) + p[1..].ToLower()));
}
