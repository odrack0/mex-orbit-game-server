// La zona radiactiva: mas alla del limite publicado del mapa la nave SIGUE
// volando (hasta Dials.RadiationMargin — el clamp de verdad esta en
// World.Session.cs), pero paga por segundo. La formula vive en Rules.cs
// (Combat.RadiationDamage), pura y sin estado; aqui solo el reloj por jugador.
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    /// <summary>Un paso por jugador, cada tick. Edge-triggered como el
    /// `Storage.IsInRadiationZone` del prototipo: entrar reinicia el contador de
    /// segundos y golpea YA (no un segundo despues, que se sentiria como que la
    /// radiacion no hace nada hasta que ya es tarde); salir lo apaga sin mas —
    /// la escalada es por exposicion CONTINUA, no una cuenta de por vida.</summary>
    private void UpdateRadiation()
    {
        foreach (var slot in _players.Values)
        {
            if (slot.Dead)
            {
                slot.InRadiationZone = false;
                slot.RadiationSeconds = 0;
                continue;
            }

            if (!Geometry.OutsideBounds(slot.Entity.X, slot.Entity.Y, map))
            {
                slot.InRadiationZone = false;
                slot.RadiationSeconds = 0;
                continue;
            }

            if (!slot.InRadiationZone)
            {
                slot.InRadiationZone = true;
                slot.RadiationSeconds = 0;
                slot.NextRadiationTick = _tick;
            }
            if (_tick < slot.NextRadiationTick) continue;
            slot.NextRadiationTick = _tick + ToTicks(Dials.RadiationTickMs);
            slot.RadiationSeconds++;

            var damage = Combat.RadiationDamage(slot.Entity, slot.RadiationSeconds);
            if (damage == 0) continue;
            // directo al casco: la radiacion no la frena el escudo (a diferencia
            // del laser — asi lo pidio el diseño)
            slot.Entity.Hp -= Math.Min(slot.Entity.Hp, damage);
            Send(slot, HeroStatsOf(slot));
            if (slot.Entity.Hp == 0)
                OnPlayerKilled(slot, slot.Entity.Id, "la radiación", DeathCause.Radiation);
        }
    }
}
