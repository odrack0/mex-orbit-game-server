// Lo que un jugador se lleva puesto al cambiar de mapa.
//
// No es "el jugador": es lo que el mapa destino necesita para reconstruirlo sin
// volver a la base de datos. Bodega y creditos van aqui a proposito — si se
// releyeran de BD, todo lo recogido desde el ultimo guardado se perderia al
// saltar, y perder carga por cruzar una puerta es justo el tipo de fallo que
// nadie reporta porque parece mala suerte.
using MexOrbit.GameServer.Data;

namespace MexOrbit.GameServer.Game;

internal sealed record PlayerSlotSnapshot(
    IClientPort Port,
    PlayerData Data,
    long SessionId,
    uint LaserDamage,
    uint MaxShield,
    Dictionary<long, uint> Cargo,
    decimal Credits,
    uint Hp);
