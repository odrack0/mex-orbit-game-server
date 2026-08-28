// Las reglas del juego, sin estado y sin mundo alrededor.
//
// Todo esto estaba enterrado dentro de metodos de mil lineas que ademas
// difundian mensajes y escribian en BD. Sacado aqui es lo que siempre fue:
// aritmetica del juego que se puede leer de un vistazo y probar sin levantar
// nada.
namespace MexOrbit.GameServer.Domain;

public static class Geometria
{
    public static double Distancia(Entity a, Entity b) => Distancia(a.X, a.Y, b.X, b.Y);

    public static double Distancia(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Un punto del circulo de radio `radio` alrededor de (x,y), recortado
    /// al mapa. Asi los bichos RODEAN en vez de amontonarse en el mismo pixel.</summary>
    public static (double X, double Y) EnElCirculo(double x, double y, double radio,
        double angulo, MapInfo mapa) =>
        (Math.Clamp(x + Math.Cos(angulo) * radio, 0, mapa.BoundsX),
         Math.Clamp(y + Math.Sin(angulo) * radio, 0, mapa.BoundsY));
}

public static class Combate
{
    /// <summary>El escudo absorbe primero; lo que sobra va al casco. Devuelve el
    /// daño efectivamente encajado.</summary>
    public static uint Encajar(Entity objetivo, uint danio)
    {
        var alEscudo = Math.Min(objetivo.Shield, danio);
        objetivo.Shield -= alEscudo;
        var alCasco = Math.Min(objetivo.Hp, danio - alEscudo);
        objetivo.Hp -= alCasco;
        return alEscudo + alCasco;
    }

    /// <summary>El legado sorteaba ±10% sobre el daño base del NPC. Se conserva.</summary>
    public static uint ConVariacion(uint danioBase, Func<int, int, int> sorteo)
    {
        var b = (int)danioBase;
        return (uint)Math.Max(1, b + sorteo(-b / 10, b / 10 + 1));
    }

    /// <summary>10% del maximo por segundo, tras `NpcOutOfCombatMs` sin recibir
    /// fuego (el `CheckShieldPointsRepair` del legado).</summary>
    public static void RegenerarEscudo(Entity npc)
    {
        if (npc.Shield >= npc.MaxShield) return;
        npc.Shield = Math.Min(npc.MaxShield, npc.Shield + Math.Max(1, npc.MaxShield / 10));
    }

    /// <summary>Un cobarde huye cuando su casco cae por debajo de su umbral.
    /// `flee_hp_pct` 0 = jamas huye.</summary>
    public static bool DebeHuir(Entity npc, byte fleeHpPct) =>
        fleeHpPct != 0 && npc.MaxHp != 0 && npc.Hp * 100 / npc.MaxHp < fleeHpPct;

    /// <summary>El rumbo de fuga: en direccion CONTRARIA a quien le estaba
    /// pegando, o uno cualquiera si ya no hay a quien dar la espalda.</summary>
    public static (double X, double Y) RumboDeHuida(Entity npc, Entity? agresor,
        MapInfo mapa, Func<double> sorteo)
    {
        var dx = agresor is null ? sorteo() * 2 - 1 : npc.X - agresor.X;
        var dy = agresor is null ? sorteo() * 2 - 1 : npc.Y - agresor.Y;
        var largo = Math.Sqrt(dx * dx + dy * dy);
        if (largo < 1) { dx = 1; dy = 0; largo = 1; }
        return (Math.Clamp(npc.X + dx / largo * Diales.HuidaDistancia, 0, mapa.BoundsX),
                Math.Clamp(npc.Y + dy / largo * Diales.HuidaDistancia, 0, mapa.BoundsY));
    }
}

public static class Botin
{
    /// <summary>El NPC pone la CANTIDAD, la zona pone la MEZCLA (§4 guidelines).</summary>
    public static Dictionary<long, uint> Repartir(uint total, IReadOnlyList<MaterialBias> bias)
    {
        var drops = new Dictionary<long, uint>();
        var pesoTotal = bias.Sum(b => b.Weight);
        if (pesoTotal == 0) return drops;
        foreach (var b in bias)
        {
            var unidades = (uint)Math.Round(total * b.Weight / pesoTotal);
            if (unidades > 0) drops[b.ItemId] = unidades;
        }
        return drops;
    }

    /// <summary>Toma de la caja lo que quepa en el hueco disponible. Lo que no
    /// cabe SIGUE en la caja: recoger nunca destruye lo que no entra.</summary>
    public static List<(long ItemId, uint Amount)> Tomar(LootBox caja, uint espacio)
    {
        var tomados = new List<(long ItemId, uint Amount)>();
        foreach (var (itemId, disponible) in caja.Drops.ToList())
        {
            if (espacio == 0) break;
            var toma = Math.Min(disponible, espacio);
            tomados.Add((itemId, toma));
            espacio -= toma;
            if (toma == disponible) caja.Drops.Remove(itemId);
            else caja.Drops[itemId] = disponible - toma;
        }
        return tomados;
    }
}
