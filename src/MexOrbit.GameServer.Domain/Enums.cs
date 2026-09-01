// El vocabulario del juego, dicho por el juego.
//
// Estos enums existen tambien en el protocolo generado, y aqui estan repetidos a
// proposito: el dominio no puede depender de como viajan las cosas por el cable.
// Si manana el wire cambia el numero de `TOO_FAR`, cambia el codec y no cambia
// una sola regla. La traduccion es explicita (un `switch` total, en la capa del
// protocolo) para que una divergencia reviente en la frontera y no se cuele como
// un byte equivocado.
namespace MexOrbit.GameServer.Domain;

public enum EntityKind { Player, Npc }

public enum DespawnReason { Range, Left, Dead }

public enum BoxDespawnReason { Collected, Expired, Range }

public enum Weapon { Laser }

public enum DeathCause { Npc, Player, Radiation }

public enum ChatChannel { Global, Faction, Clan }

public enum ErrorCode
{
    Generic,
    BadTicket,
    VersionUnsupported,
    Banned,
    ResumeExpired,
    TooFar,
    Gone,
    Insufficient,
    RateLimited,
    Invalid,
}
