// Las reglas del juego, sin estado y sin mundo alrededor.
//
// Todo esto estaba enterrado dentro de metodos de mil lineas que ademas
// difundian mensajes y escribian en BD. Sacado aqui es lo que siempre fue:
// aritmetica del juego que se puede leer de un vistazo y probar sin levantar
// nada.
namespace MexOrbit.GameServer.Domain;

public static class Geometry
{
    public static double Distance(Entity a, Entity b) => Distance(a.X, a.Y, b.X, b.Y);

    public static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Fuera de los limites publicados del mapa: la zona radiactiva
    /// (hasta donde se puede llegar de verdad lo dice Dials.RadiationMargin).</summary>
    public static bool OutsideBounds(double x, double y, MapInfo map) =>
        x < 0 || x > map.BoundsX || y < 0 || y > map.BoundsY;

    /// <summary>Un punto del circulo de radio `radio` alrededor de (x,y), recortado
    /// al mapa. Asi los bichos RODEAN en vez de amontonarse en el mismo pixel.</summary>
    public static (double X, double Y) OnCircle(double x, double y, double radius,
        double angle, MapInfo map) =>
        (Math.Clamp(x + Math.Cos(angle) * radius, 0, map.BoundsX),
         Math.Clamp(y + Math.Sin(angle) * radius, 0, map.BoundsY));
}

public static class Combat
{
    /// <summary>El escudo absorbe primero; lo que sobra va al casco. Devuelve el
    /// daño efectivamente encajado.</summary>
    public static uint Absorb(Entity target, uint damage)
    {
        var toShield = Math.Min(target.Shield, damage);
        target.Shield -= toShield;
        var toHull = Math.Min(target.Hp, damage - toShield);
        target.Hp -= toHull;
        return toShield + toHull;
    }

    /// <summary>El legado sorteaba ±10% sobre el daño base del NPC. Se conserva.</summary>
    public static uint WithVariance(uint baseDamage, Func<int, int, int> roll)
    {
        var b = (int)baseDamage;
        return (uint)Math.Max(1, b + roll(-b / 10, b / 10 + 1));
    }

    /// <summary>10% del maximo por segundo, tras `NpcOutOfCombatMs` sin recibir
    /// fuego (el `CheckShieldPointsRepair` del legado).</summary>
    public static void RegenerateShield(Entity npc)
    {
        if (npc.Shield >= npc.MaxShield) return;
        npc.Shield = Math.Min(npc.MaxShield, npc.Shield + Math.Max(1, npc.MaxShield / 10));
    }

    /// <summary>Cuanto cobra la radiacion en el segundo N de exposicion
    /// CONTINUA (1, 2, 3...): 10%, 11%, 12%... del casco MAXIMO, directo — a
    /// diferencia del laser, aqui el escudo no absorbe nada.</summary>
    public static uint RadiationDamage(Entity ship, uint secondsInZone)
    {
        var pct = Dials.RadiationInitialPct + (secondsInZone - 1) * Dials.RadiationEscalationPct;
        return (uint)Math.Min(ship.MaxHp, Math.Round(ship.MaxHp * pct / 100.0));
    }

    /// <summary>Un cobarde huye cuando su casco cae por debajo de su umbral.
    /// `flee_hp_pct` 0 = jamas huye.</summary>
    public static bool ShouldFlee(Entity npc, byte fleeHpPct) =>
        fleeHpPct != 0 && npc.MaxHp != 0 && npc.Hp * 100 / npc.MaxHp < fleeHpPct;

    /// <summary>El rumbo de fuga: en direccion CONTRARIA a quien le estaba
    /// pegando, o uno cualquiera si ya no hay a quien dar la espalda.</summary>
    public static (double X, double Y) FleeHeading(Entity npc, Entity? attacker,
        MapInfo map, Func<double> roll)
    {
        var dx = attacker is null ? roll() * 2 - 1 : npc.X - attacker.X;
        var dy = attacker is null ? roll() * 2 - 1 : npc.Y - attacker.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) { dx = 1; dy = 0; length = 1; }
        return (Math.Clamp(npc.X + dx / length * Dials.FleeDistance, 0, map.BoundsX),
                Math.Clamp(npc.Y + dy / length * Dials.FleeDistance, 0, map.BoundsY));
    }
}

public static class Loot
{
    /// <summary>El NPC pone la CANTIDAD, la zona pone la MEZCLA (§4 guidelines).</summary>
    public static Dictionary<long, uint> Distribute(uint total, IReadOnlyList<MaterialBias> bias)
    {
        var drops = new Dictionary<long, uint>();
        var totalWeight = bias.Sum(b => b.Weight);
        if (totalWeight == 0) return drops;
        foreach (var b in bias)
        {
            var units = (uint)Math.Round(total * b.Weight / totalWeight);
            if (units > 0) drops[b.ItemId] = units;
        }
        return drops;
    }

    /// <summary>Toma de la caja lo que quepa en el hueco disponible. Lo que no
    /// cabe SIGUE en la caja: recoger nunca destruye lo que no entra.</summary>
    public static List<(long ItemId, uint Amount)> Take(LootBox box, uint space)
    {
        var taken = new List<(long ItemId, uint Amount)>();
        foreach (var (itemId, available) in box.Drops.ToList())
        {
            if (space == 0) break;
            var toma = Math.Min(available, space);
            taken.Add((itemId, toma));
            space -= toma;
            if (toma == available) box.Drops.Remove(itemId);
            else box.Drops[itemId] = available - toma;
        }
        return taken;
    }
}

/// <summary>Las zonas santuario del mapa: el circulo de la estacion y el de cada
/// portal — exactamente los que el cliente PINTA, para que la regla coincida con
/// lo que el jugador ve.
///
/// Un NPC no entra ahi por su cuenta: ni eligiendo destino de vagabundeo, ni
/// colocandose junto a una presa, ni nace dentro. La UNICA llave es la
/// provocacion: si el jugador abre fuego, su agresor puede cruzar (la misma
/// regla del DMZ — el refugio no es un parapeto desde el que disparar gratis).</summary>
public sealed class SafeZones
{
    private readonly List<(double X, double Y, double R)> _zones = [];

    public static SafeZones Of(MapInfo map, IEnumerable<PortalInfo> portals, double portalRadius)
    {
        var zones = new SafeZones();
        if (map.SecureRange > 0) zones._zones.Add((map.StationX, map.StationY, map.SecureRange));
        foreach (var p in portals) zones._zones.Add((p.X, p.Y, portalRadius));
        return zones;
    }

    public bool Inside(double x, double y) =>
        _zones.Any(z => Geometry.Distance(x, y, z.X, z.Y) < z.R);

    /// <summary>El punto mas cercano FUERA de la zona que contiene a (x,y), con
    /// margen. En el centro exacto no hay direccion de salida: la pone el sorteo.</summary>
    public (double X, double Y) NearestExit(double x, double y, double margin, Func<double> roll)
    {
        foreach (var (zx, zy, r) in _zones)
        {
            var dist = Geometry.Distance(x, y, zx, zy);
            if (dist >= r) continue;
            double dx, dy;
            if (dist < 1) { var a = roll() * Math.PI * 2; dx = Math.Cos(a); dy = Math.Sin(a); }
            else { dx = (x - zx) / dist; dy = (y - zy) / dist; }
            return (zx + dx * (r + margin), zy + dy * (r + margin));
        }
        return (x, y);
    }
}
