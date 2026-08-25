// Entidades del mundo: jugadores y NPCs, sobre el mismo modelo de movimiento.
// Convencion de ids: jugador = account_id · NPC = 1_000_000 + n (documentado en el cliente).
using MexOrbit.Protocol;

namespace MexOrbit.GameServer.Game;

public sealed class Entity
{
    public required ulong Id { get; init; }
    public required EntityKind Kind { get; init; }
    public required string TypeId { get; init; }
    public required string Name { get; init; }
    public uint Faction { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public double TargetX { get; set; }
    public double TargetY { get; set; }
    public required uint Speed { get; init; }          // unidades por segundo
    public uint Hp { get; set; }
    public uint MaxHp { get; init; }

    public bool Moving => Math.Abs(X - TargetX) > 0.5 || Math.Abs(Y - TargetY) > 0.5;

    /// <summary>Avanza hacia el target a la velocidad de la entidad. Devuelve true si se movio.</summary>
    public bool Step(double dtSeconds)
    {
        if (!Moving) return false;
        var dx = TargetX - X;
        var dy = TargetY - Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var paso = Speed * dtSeconds;
        if (paso >= dist) { X = TargetX; Y = TargetY; }
        else { X += dx / dist * paso; Y += dy / dist * paso; }
        return true;
    }

    public EntitySpawn ToSpawn() => new()
    {
        EntityId = Id,
        Kind = Kind,
        TypeId = TypeId,
        Name = Name,
        Faction = Faction,
        X = (ulong)Math.Round(X),
        Y = (ulong)Math.Round(Y),
        HpPct = MaxHp == 0 ? 1f : (float)Hp / MaxHp,
        Speed = Speed,
    };

    public EntityMove ToMove(bool teleport = false) => new()
    {
        EntityId = Id,
        X = (ulong)Math.Round(X),
        Y = (ulong)Math.Round(Y),
        TargetX = (ulong)Math.Round(TargetX),
        TargetY = (ulong)Math.Round(TargetY),
        Speed = Speed,
        Teleport = teleport,
    };
}
