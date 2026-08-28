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
public sealed record Enlace(World Mundo, long AccountId);

public sealed class Handshake(Universe universo, IPlayerRepository players,
    ISessionRepository sessions, ITicketVerifier verifier, IServerCodec codec, IClock clock,
    int protocolVersion, int tickMs)
{
    /// <summary>Entrar con un game ticket. Devuelve el mundo al que queda atada la
    /// conexion, o null si se rechazo (el motivo ya viajo al cliente).</summary>
    public Enlace? Entrar(IClientPort port, string gameTicket, ulong versionDelCliente)
    {
        if (!VersionCompatible(port, versionDelCliente)) return null;

        var (accountId, error) = verifier.Verify(gameTicket, protocolVersion);
        if (error is not null)
        {
            Enviar(port, new Failed(0, error.Value));
            return null;
        }

        var player = players.LoadPlayer(accountId);
        if (player is null)
        {
            Enviar(port, new Failed(0, ErrorCode.Generic, "cuenta sin nave"));
            return null;
        }
        // Se entra DONDE SE DEJO el juego, no siempre en el mapa inicial. Antes
        // daba igual porque solo habia un mapa; en cuanto hay dos, entrar
        // siempre en el 1-1 teletransporta a quien cerro sesion en otro sitio.
        var mundo = universo.DondeEsta(player.MapId) ?? universo.Inicial();

        var laserDamage = players.LoadLaserDamage(accountId);
        var maxShield = players.LoadShieldCapacity(accountId);
        var cargo = players.LoadCargo(accountId);
        var (sessionId, reconnectToken) = sessions.OpenSession(accountId);

        Enviar(port, new Welcomed(accountId, reconnectToken, (ulong)clock.UnixMs,
            (uint)(1000 / tickMs)));
        mundo.Post(new JoinCmd(port, player, sessionId, laserDamage, maxShield, cargo));
        return new Enlace(mundo, accountId);
    }

    /// <summary>Volver con el token de reconexion entregado en el Welcome.</summary>
    public Enlace? Volver(IClientPort port, string reconnectToken, ulong versionDelCliente)
    {
        if (!VersionCompatible(port, versionDelCliente)) return null;

        var sesion = sessions.FindSessionByToken(reconnectToken);
        if (sesion is null)
        {
            Enviar(port, new Failed(0, ErrorCode.ResumeExpired));
            return null;
        }
        // El mundo sale de DONDE DICE LA BD que esta el jugador, y esto es lo que
        // hace que el salto funcione sin credencial nueva: el origen ya lo
        // persistio en el mapa destino antes de soltarlo.
        var quien = players.LoadPlayer(sesion.Value.AccountId);
        var mundo = quien is null ? universo.Inicial()
            : universo.DondeEsta(quien.MapId) ?? universo.Inicial();

        // Se manda tambien CON QUE reconstruirlo: si este mundo no lo ha visto
        // nunca —que es justo el caso al llegar de otro mapa— entra de cero en
        // vez de recibir un RESUME_EXPIRED.
        mundo.Post(new ResumeCmd(port, sesion.Value.AccountId, sesion.Value.SessionId, quien,
            quien is null ? 0 : players.LoadLaserDamage(sesion.Value.AccountId),
            quien is null ? 0 : players.LoadShieldCapacity(sesion.Value.AccountId),
            quien is null ? null : players.LoadCargo(sesion.Value.AccountId)));
        return new Enlace(mundo, sesion.Value.AccountId);
    }

    /// <summary>El mundo al que apuntar antes de saber quien llama.</summary>
    public World Inicial() => universo.Inicial();

    private bool VersionCompatible(IClientPort port, ulong versionDelCliente)
    {
        if (versionDelCliente == (ulong)protocolVersion) return true;
        Enviar(port, new Failed(0, ErrorCode.VersionUnsupported));
        return false;
    }

    private void Enviar(IClientPort port, ServerEvent evento) => port.Send(codec.Encode(evento));
}
