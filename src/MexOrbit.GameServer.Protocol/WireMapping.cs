// La traduccion del vocabulario: dominio <-> cable.
//
// Son `switch` TOTALES a proposito. Podrian ser un cast de enteros —los valores
// coinciden hoy— pero entonces el dia que el esquema renumere un codigo, el
// server mandaria silenciosamente el byte equivocado. Asi revienta en la
// frontera, que es donde se puede ver.
using D = MexOrbit.GameServer.Domain;
using W = MexOrbit.Protocol;

namespace MexOrbit.GameServer.Protocol;

internal static class WireMapping
{
    // ─── saliente: dominio -> cable ─────────────────────────────────────────

    public static W.EntityKind ToWire(D.EntityKind v) => v switch
    {
        D.EntityKind.Player => W.EntityKind.Player,
        D.EntityKind.Npc => W.EntityKind.Npc,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "EntityKind sin traduccion"),
    };

    public static W.DespawnReason ToWire(D.DespawnReason v) => v switch
    {
        D.DespawnReason.Range => W.DespawnReason.Range,
        D.DespawnReason.Left => W.DespawnReason.Left,
        D.DespawnReason.Dead => W.DespawnReason.Dead,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "DespawnReason sin traduccion"),
    };

    public static W.BoxDespawnReason ToWire(D.BoxDespawnReason v) => v switch
    {
        D.BoxDespawnReason.Collected => W.BoxDespawnReason.Collected,
        D.BoxDespawnReason.Expired => W.BoxDespawnReason.Expired,
        D.BoxDespawnReason.Range => W.BoxDespawnReason.Range,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "BoxDespawnReason sin traduccion"),
    };

    public static W.Weapon ToWire(D.Weapon v) => v switch
    {
        D.Weapon.Laser => W.Weapon.Laser,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Weapon sin traduccion"),
    };

    public static W.DeathCause ToWire(D.DeathCause v) => v switch
    {
        D.DeathCause.Npc => W.DeathCause.Npc,
        D.DeathCause.Player => W.DeathCause.Player,
        D.DeathCause.Radiation => W.DeathCause.Radiation,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "DeathCause sin traduccion"),
    };

    public static W.ChatChannel ToWire(D.ChatChannel v) => v switch
    {
        D.ChatChannel.Global => W.ChatChannel.Global,
        D.ChatChannel.Faction => W.ChatChannel.Faction,
        D.ChatChannel.Clan => W.ChatChannel.Clan,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "ChatChannel sin traduccion"),
    };

    public static W.ErrorCode ToWire(D.ErrorCode v) => v switch
    {
        D.ErrorCode.Generic => W.ErrorCode.Generic,
        D.ErrorCode.BadTicket => W.ErrorCode.BadTicket,
        D.ErrorCode.VersionUnsupported => W.ErrorCode.VersionUnsupported,
        D.ErrorCode.Banned => W.ErrorCode.Banned,
        D.ErrorCode.ResumeExpired => W.ErrorCode.ResumeExpired,
        D.ErrorCode.TooFar => W.ErrorCode.TooFar,
        D.ErrorCode.Gone => W.ErrorCode.Gone,
        D.ErrorCode.Insufficient => W.ErrorCode.Insufficient,
        D.ErrorCode.RateLimited => W.ErrorCode.RateLimited,
        D.ErrorCode.Invalid => W.ErrorCode.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, "ErrorCode sin traduccion"),
    };

    // ─── entrante: cable -> dominio ─────────────────────────────────────────

    public static D.ChatChannel ToDomain(W.ChatChannel v) => v switch
    {
        W.ChatChannel.Global => D.ChatChannel.Global,
        W.ChatChannel.Faction => D.ChatChannel.Faction,
        W.ChatChannel.Clan => D.ChatChannel.Clan,
        // el cliente puede mentir: un canal que no existe se trata como global,
        // nunca como una excepcion que le tumbe la sesion
        _ => D.ChatChannel.Global,
    };

    /// <summary>Las coordenadas viajan enteras: el mundo simula en doubles y el
    /// cable no necesita esa precision. Con SIGNO: la zona radiactiva por el
    /// lado del 0 es negativa, y el `Math.Max(0, v)` que habia aqui era una de
    /// las cinco capas que la dejaban en pared.</summary>
    public static long Round(double v) => (long)Math.Round(v);
}
