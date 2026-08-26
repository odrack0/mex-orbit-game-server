// Una conexion WebSocket: handshake (Hello + ticket), loop de recepcion con
// dispatch por msg_id, y cola de envio propia. La conexion NO toca el estado
// del mundo: postea comandos al inbox y transmite lo que el mundo le entregue.
using System.Net.WebSockets;
using System.Threading.Channels;
using MexOrbit.GameServer.Data;
using MexOrbit.GameServer.Game;
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Net;

public sealed class ClientConnection(WebSocket socket, Universe universo, Repo repo, TicketVerifier verifier,
    int protocolVersion, ILogger log) : IClientPort
{
    private const int MaxFrame = 64 * 1024;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();

    // El mundo de una conexion NO es fijo: cambia al saltar de sector. El Universo
    // avisa cuando se muda, porque si los comandos siguieran yendo al mapa viejo
    // el jugador quedaria manejando un fantasma.
    private World _world = universo.Inicial();

    public long AccountId { get; private set; }


    public void Send(byte[] frame) => _outbox.Writer.TryWrite(frame);

    public void CloseSocket() => _cts.Cancel();

    public async Task RunAsync()
    {
        var envio = Task.Run(SendLoopAsync);
        try
        {
            // ---- handshake: el primer frame es Hello (entrar) o Resume (volver) ----
            var primero = await ReceiveFrameAsync();
            if (primero is null) return;

            if (LeerMsgId(primero) == Resume.MsgId)
            {
                var resume = Resume.Decode(primero);
                if (resume.ProtocolVersion != (ulong)protocolVersion)
                {
                    Send(new ErrorReply { Code = ErrorCode.VersionUnsupported }.Encode());
                    return;
                }
                var sesion = repo.FindSessionByToken(resume.ReconnectToken);
                if (sesion is null)
                {
                    Send(new ErrorReply { Code = ErrorCode.ResumeExpired }.Encode());
                    return;
                }
                AccountId = sesion.Value.AccountId;
                // El mundo sale de DONDE DICE LA BD que esta el jugador, y esto es
                // lo que hace que el salto funcione sin credencial nueva: el origen
                // ya lo persistio en el mapa destino antes de soltarlo.
                var quien = repo.LoadPlayer(sesion.Value.AccountId);
                if (quien is not null) _world = universo.DondeEsta(quien.MapId) ?? _world;
                // Se manda tambien con QUE reconstruirlo: si este mundo no lo ha
                // visto nunca —que es justo el caso al llegar de otro mapa— entra
                // de cero en vez de recibir un RESUME_EXPIRED.
                _world.Post(new ResumeCmd(this, sesion.Value.AccountId, sesion.Value.SessionId,
                    quien, quien is null ? 0 : repo.LoadLaserDamage(sesion.Value.AccountId),
                    quien is null ? 0 : repo.LoadShieldCapacity(sesion.Value.AccountId),
                    quien is null ? null : repo.LoadCargo(sesion.Value.AccountId)));
                await LoopAsync();
                return;
            }

            if (LeerMsgId(primero) != Hello.MsgId)
            {
                Send(new ErrorReply { Code = ErrorCode.Invalid, Detail = "se esperaba Hello o Resume" }.Encode());
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
            // Se entra DONDE SE DEJO el juego, no siempre en el mapa inicial. Antes
            // daba igual porque solo habia un mapa; en cuanto hay dos, entrar
            // siempre en el 1-1 teletransporta a quien cerro sesion en otro sitio.
            if (player is not null) _world = universo.DondeEsta(player.MapId) ?? _world;
            if (player is null)
            {
                Send(new ErrorReply { Code = ErrorCode.Generic, Detail = "cuenta sin nave" }.Encode());
                return;
            }
            AccountId = accountId;
            var laserDamage = repo.LoadLaserDamage(accountId);
            var maxShield = repo.LoadShieldCapacity(accountId);
            var cargo = repo.LoadCargo(accountId);
            var (sessionId, reconnectToken) = repo.OpenSession(accountId);

            Send(new Welcome
            {
                AccountId = (ulong)accountId,
                ReconnectToken = reconnectToken,
                ServerTimeMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                TickRate = 1000 / 80u,
            }.Encode());
            _world.Post(new JoinCmd(this, player, sessionId, laserDamage, maxShield, cargo));
            await LoopAsync();
        }
        catch (OperationCanceledException) { /* cierre pedido por el mundo */ }
        catch (WebSocketException) { /* socket caido: el mundo hara el drop */ }
        finally
        {
            // DROPPED (no LOGOUT): una caida abre la ventana de gracia; solo el
            // LogoutRequest explicito saca la nave del mundo
            _world.Post(new LeaveCmd(this, "DROPPED"));
            _outbox.Writer.TryComplete();
            try { await envio; } catch { /* ya cerrando */ }
            socket.Dispose();
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
            switch (LeerMsgId(frame))
            {
                case MoveIntent.MsgId: _world.Post(new MoveIntentCmd(this, MoveIntent.Decode(frame))); break;
                case Pong.MsgId: _world.Post(new PongCmd(this, Pong.Decode(frame).Nonce)); break;
                case SelectTarget.MsgId: _world.Post(new SelectTargetCmd(this, SelectTarget.Decode(frame).EntityId)); break;
                case LaserToggle.MsgId: _world.Post(new LaserToggleCmd(this, LaserToggle.Decode(frame).Active)); break;
                case CollectBox.MsgId:
                    var cb = CollectBox.Decode(frame);
                    _world.Post(new CollectBoxCmd(this, cb.RequestId, cb.BoxId));
                    break;
                case UnloadCargo.MsgId:
                    _world.Post(new UnloadCargoCmd(this, UnloadCargo.Decode(frame).RequestId));
                    break;
                case SellToNpc.MsgId:
                    var sn = SellToNpc.Decode(frame);
                    _world.Post(new SellToNpcCmd(this, sn.RequestId, sn.MaterialId, sn.Amount));
                    break;
                case RespawnSelect.MsgId:
                    _world.Post(new RespawnSelectCmd(this, RespawnSelect.Decode(frame).OptionId));
                    break;
                case ChatSend.MsgId:
                    var cs = ChatSend.Decode(frame);
                    _world.Post(new ChatSendCmd(this, cs.RequestId, cs.Channel, cs.Text));
                    break;
                case JumpRequest.MsgId:
                    var jr = JumpRequest.Decode(frame);
                    _world.Post(new JumpCmd(this, jr.RequestId, jr.PortalId));
                    break;
                case LogoutRequest.MsgId: _world.Post(new LeaveCmd(this, "LOGOUT")); _cts.Cancel(); break;
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
