// El apreton de manos: quien eres, en que version hablas y a que mundo entras.
//
// Esto vivia dentro de `ClientConnection`, mezclado con el bucle de recepcion del
// socket y la lectura de varints. Son dos cosas distintas: una es TRANSPORTE
// —bytes que entran y salen— y la otra es una decision de negocio con reglas
// (sesion unica, ticket de un solo uso, se entra DONDE SE DEJO el juego). La
// segunda es la que esta aqui, y por eso ahora se puede probar sin socket.
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Application;

/// <summary>A que mundo quedo atada la conexion y de quien es.</summary>
public sealed record HandshakeResult(World World, long AccountId);

public sealed class Handshake(Universe universe, IPlayerRepository players,
    ISessionRepository sessions, ITicketVerifier verifier, IServerCodec codec, IClock clock,
    int protocolVersion, int tickMs)
{
    /// <summary>Entrar con un game ticket. Devuelve el mundo al que queda atada la
    /// conexion, o null si se rechazo (el motivo ya viajo al cliente).</summary>
    public HandshakeResult? Enter(IClientPort port, string gameTicket, ulong clientProtocolVersion)
    {
        if (!IsVersionSupported(port, clientProtocolVersion)) return null;

        var (accountId, error) = verifier.Verify(gameTicket, protocolVersion);
        if (error is not null)
        {
            Send(port, new Failed(0, error.Value));
            return null;
        }

        var player = players.LoadPlayer(accountId);
        if (player is null)
        {
            Send(port, new Failed(0, ErrorCode.Generic, "cuenta sin nave"));
            return null;
        }
        // Se entra DONDE SE DEJO el juego, no siempre en el mapa inicial. Antes
        // daba igual porque solo habia un mapa; en cuanto hay dos, entrar
        // siempre en el 1-1 teletransporta a quien cerro sesion en otro sitio.
        var world = universe.WhereIs(player.MapId) ?? universe.Starter();

        var laserDamage = players.LoadLaserDamage(accountId);
        var maxShield = players.LoadShieldCapacity(accountId);
        var cargo = players.LoadCargo(accountId);
        var (sessionId, reconnectToken) = sessions.OpenSession(accountId);

        Send(port, new Welcomed(accountId, reconnectToken, (ulong)clock.UnixMs,
            (uint)(1000 / tickMs)));
        world.Post(new JoinCmd(port, player, sessionId, laserDamage, maxShield, cargo));
        return new HandshakeResult(world, accountId);
    }

    /// <summary>Volver con el token de reconexion entregado en el Welcome.</summary>
    public HandshakeResult? Resume(IClientPort port, string reconnectToken, ulong clientProtocolVersion)
    {
        if (!IsVersionSupported(port, clientProtocolVersion)) return null;

        var session = sessions.FindSessionByToken(reconnectToken);
        if (session is null)
        {
            Send(port, new Failed(0, ErrorCode.ResumeExpired));
            return null;
        }
        // El mundo sale de DONDE DICE LA BD que esta el jugador, y esto es lo que
        // hace que el salto funcione sin credencial nueva: el origen ya lo
        // persistio en el mapa destino antes de soltarlo.
        var who = players.LoadPlayer(session.Value.AccountId);
        var world = who is null ? universe.Starter()
            : universe.WhereIs(who.MapId) ?? universe.Starter();

        // Se manda tambien CON QUE reconstruirlo: si este mundo no lo ha visto
        // nunca —que es justo el caso al llegar de otro mapa— entra de cero en
        // vez de recibir un RESUME_EXPIRED.
        world.Post(new ResumeCmd(port, session.Value.AccountId, session.Value.SessionId, who,
            who is null ? 0 : players.LoadLaserDamage(session.Value.AccountId),
            who is null ? 0 : players.LoadShieldCapacity(session.Value.AccountId),
            who is null ? null : players.LoadCargo(session.Value.AccountId)));
        return new HandshakeResult(world, session.Value.AccountId);
    }

    /// <summary>El mundo al que apuntar antes de saber quien llama.</summary>
    public World Starter() => universe.Starter();

    private bool IsVersionSupported(IClientPort port, ulong clientProtocolVersion)
    {
        if (clientProtocolVersion == (ulong)protocolVersion) return true;
        Send(port, new Failed(0, ErrorCode.VersionUnsupported));
        return false;
    }

    private void Send(IClientPort port, ServerEvent serverEvent) => port.Send(codec.Encode(serverEvent));
}
