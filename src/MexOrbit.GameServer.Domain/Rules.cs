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
