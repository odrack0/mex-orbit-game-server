// Lo que el mundo ACEPTA. Un comando es una intencion ya validada como frame y
// ya traducida: cuando llega aqui no queda nada del cable, solo el que lo pide y
// lo que pide.
//
// `MoveIntentCmd` traia dentro el mensaje `MexOrbit.Protocol.MoveIntent` tal
// cual, asi que el mundo no podia recibir una orden de moverse sin que existiera
// el protocolo binario. Ahora trae tres numeros.
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Application;

public abstract record WorldCmd(IClientPort Port);

public sealed record JoinCmd(IClientPort Port, PlayerData Player, long SessionId, uint LaserDamage,
    uint MaxShield, Dictionary<long, uint> Cargo) : WorldCmd(Port);

/// <summary>Reconexion. Trae CON QUE reconstruir al jugador: si este mundo no lo
/// ha visto nunca —que es justo el caso al llegar de otro mapa— entra de cero en
/// vez de recibir un RESUME_EXPIRED.</summary>
public sealed record ResumeCmd(IClientPort Port, long AccountId, long SessionId,
    PlayerData? Player, uint LaserDamage, uint MaxShield, Dictionary<long, uint>? Cargo)
    : WorldCmd(Port);

public sealed record LeaveCmd(IClientPort Port, string Reason) : WorldCmd(Port);

public sealed record MoveIntentCmd(IClientPort Port, ulong Seq, uint TargetX, uint TargetY)
    : WorldCmd(Port);

public sealed record PongCmd(IClientPort Port, ulong Nonce) : WorldCmd(Port);
public sealed record SelectTargetCmd(IClientPort Port, ulong EntityId) : WorldCmd(Port);
public sealed record LaserToggleCmd(IClientPort Port, bool Active) : WorldCmd(Port);
public sealed record CollectBoxCmd(IClientPort Port, ulong RequestId, ulong BoxId) : WorldCmd(Port);
public sealed record UnloadCargoCmd(IClientPort Port, ulong RequestId) : WorldCmd(Port);

public sealed record SellToNpcCmd(IClientPort Port, ulong RequestId, string MaterialId, ulong Amount)
    : WorldCmd(Port);

public sealed record ChatSendCmd(IClientPort Port, ulong RequestId, ChatChannel Channel, string Text)
    : WorldCmd(Port);

public sealed record RespawnSelectCmd(IClientPort Port, ulong OptionId) : WorldCmd(Port);
public sealed record JumpCmd(IClientPort Port, ulong RequestId, ulong PortalId) : WorldCmd(Port);
