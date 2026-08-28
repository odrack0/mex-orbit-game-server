// El traductor de entrada: un frame del cliente, un comando del mundo.
//
// Estaba dentro de `ClientConnection`, mezclado con el bucle del socket. Sacarlo
// tiene una consecuencia concreta: el mundo ya no recibe mensajes del protocolo
// —recibia el `MoveIntent` generado tal cual— sino intenciones suyas.
using MexOrbit.GameServer.Application;
using W = MexOrbit.Protocol;

namespace MexOrbit.GameServer.Protocol;

public static class ClientFrames
{
    /// <summary>El varint de cabecera. Los ids del catalogo caben en 2 bytes.</summary>
    public static int MsgId(ReadOnlySpan<byte> frame)
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

    public static bool IsHello(ReadOnlySpan<byte> frame) => MsgId(frame) == W.Hello.MsgId;
    public static bool IsResume(ReadOnlySpan<byte> frame) => MsgId(frame) == W.Resume.MsgId;

    public static (ulong Version, string GameTicket) ReadHello(byte[] frame)
    {
        var hello = W.Hello.Decode(frame);
        return (hello.ProtocolVersion, hello.GameTicket);
    }

    public static (ulong Version, string ReconnectToken) ReadResume(byte[] frame)
    {
        var resume = W.Resume.Decode(frame);
        return (resume.ProtocolVersion, resume.ReconnectToken);
    }

    /// <summary>Traduce un frame de juego a su comando. `null` = mensaje
    /// desconocido o fuera de lugar: se ignora, jamas rompe la sesion.
    ///
    /// Lanza <see cref="W.ProtocolViolationException"/> si el frame viola el
    /// contrato; quien llama decide que contarle al cliente.</summary>
    public static WorldCmd? Read(IClientPort port, byte[] frame) => MsgId(frame) switch
    {
        W.MoveIntent.MsgId => Move(port, frame),
        W.Pong.MsgId => new PongCmd(port, W.Pong.Decode(frame).Nonce),
        W.SelectTarget.MsgId => new SelectTargetCmd(port, W.SelectTarget.Decode(frame).EntityId),
        W.LaserToggle.MsgId => new LaserToggleCmd(port, W.LaserToggle.Decode(frame).Active),
        W.CollectBox.MsgId => Collect(port, frame),
        W.UnloadCargo.MsgId => new UnloadCargoCmd(port, W.UnloadCargo.Decode(frame).RequestId),
        W.SellToNpc.MsgId => Sell(port, frame),
        W.RespawnSelect.MsgId => new RespawnSelectCmd(port, W.RespawnSelect.Decode(frame).OptionId),
        W.ChatSend.MsgId => Chat(port, frame),
        W.JumpRequest.MsgId => Jump(port, frame),
        W.LogoutRequest.MsgId => new LeaveCmd(port, "LOGOUT"),
        _ => null,
    };

    /// <summary>Un LogoutRequest ademas cuelga: el mundo no cierra sockets ajenos.</summary>
    public static bool IsLogout(ReadOnlySpan<byte> frame) => MsgId(frame) == W.LogoutRequest.MsgId;

    private static WorldCmd Move(IClientPort port, byte[] frame)
    {
        var m = W.MoveIntent.Decode(frame);
        return new MoveIntentCmd(port, m.Seq, (uint)m.TargetX, (uint)m.TargetY);
    }

    private static WorldCmd Collect(IClientPort port, byte[] frame)
    {
        var m = W.CollectBox.Decode(frame);
        return new CollectBoxCmd(port, m.RequestId, m.BoxId);
    }

    private static WorldCmd Sell(IClientPort port, byte[] frame)
    {
        var m = W.SellToNpc.Decode(frame);
        return new SellToNpcCmd(port, m.RequestId, m.MaterialId, m.Amount);
    }

    private static WorldCmd Chat(IClientPort port, byte[] frame)
    {
        var m = W.ChatSend.Decode(frame);
        return new ChatSendCmd(port, m.RequestId, WireMapping.ToDomain(m.Channel), m.Text);
    }

    private static WorldCmd Jump(IClientPort port, byte[] frame)
    {
        var m = W.JumpRequest.Decode(frame);
        return new JumpCmd(port, m.RequestId, m.PortalId);
    }
}
