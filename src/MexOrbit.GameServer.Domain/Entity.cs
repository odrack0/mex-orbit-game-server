// Entidades del mundo: jugadores y NPCs, sobre el mismo modelo de movimiento.
// Convencion de ids: jugador = account_id · NPC = 1_000_000 + n (documentado en el cliente).
//
// Ya no sabe convertirse en mensajes: `ToSpawn()` y `ToMove()` vivian aqui y
// metian el protocolo binario dentro del dominio. Ahora la nave es una nave, y
// quien la pone en el cable es el codec.
namespace MexOrbit.GameServer.Domain;

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
    public uint Shield { get; set; }
    public uint MaxShield { get; init; }
    public long LastHitTick { get; set; } = long.MinValue;

    public bool Moving => Math.Abs(X - TargetX) > 0.5 || Math.Abs(Y - TargetY) > 0.5;

    public float HpPct => MaxHp == 0 ? 1f : (float)Hp / MaxHp;

    /// <summary>Casco y escudo viajan por separado: son dos barras, no una suma.</summary>
    public float ShieldPct => MaxShield == 0 ? 0f : (float)Shield / MaxShield;

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

    /// <summary>La planta donde este. Lo usa el golpe: frena en seco.</summary>
    public void Detener()
    {
        TargetX = X;
        TargetY = Y;
    }
}
