// La IA de los NPC y su combate: la maquina de tres estados portada del legado
// (ver NpcAi.cs en el dominio), un pensamiento por segundo.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    public void SpawnNpcs()
    {
        ulong nextId = 1_000_000;
        foreach (var spawn in npcSpawns)
            for (var i = 0; i < spawn.Amount; i++)
                SpawnNpc(spawn, nextId++);
        log.LogInformation("Mapa {code}: {n} NPCs poblados · combate NPC->jugador {estado}",
            map.Code, _npcs.Count, npcCombatEnabled ? "ENCENDIDO" : "APAGADO");
    }

    private Entity SpawnNpc(NpcSpawnInfo spawn, ulong id)
    {
        var e = new Entity
        {
            Id = id,
            Kind = EntityKind.Npc,
            TypeId = spawn.Code,
            Name = spawn.DisplayName,
            Speed = spawn.Speed,
            Hp = spawn.MaxHp,
            MaxHp = spawn.MaxHp,
            Shield = spawn.MaxShield,
            MaxShield = spawn.MaxShield,
            X = 0, Y = 0,
        };
        var (x, y) = SpawnPoint();
        e.X = x;
        e.Y = y;
        e.Stop();
        _npcs[e.Id] = e;
        _npcInfo[e.Id] = spawn;
        _npcAi[e.Id] = new NpcAi
        {
            NextThinkTick = _tick + _rng.Next(0, ToTicks(Dials.AiThinkMs)),
        };
        return e;
    }

    /// <summary>Donde nace —o renace— un bicho: un punto del mapa que NO le caiga
    /// encima a nadie.
    ///
    /// Un NPC reaparece 30 s despues de morir en un punto sorteado, y el sorteo
    /// no miraba a nadie: podia materializarse a 500 unidades de un jugador, en
    /// mitad de su pantalla y de la nada. Ahora nace fuera del rango de
    /// relevancia de todos, asi que siempre entra en escena volando desde fuera.
    ///
    /// Se intenta unas cuantas veces y se acepta lo que salga: en un mapa
    /// pequeño —o lleno de gente— puede no existir ningun punto libre, y un
    /// bicho que no reaparece seria peor que uno que aparece cerca.</summary>
    private (double X, double Y) SpawnPoint()
    {
        (double X, double Y) point = (0, 0);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            point = (MapPointX(), MapPointY());
            if (_players.Values.All(s =>
                    Geometry.Distance(point.X, point.Y, s.Entity.X, s.Entity.Y) > ranges.Entities))
                return point;
        }
        return point;
    }

    /// <summary>Los limites salen del MAPA. El legado los llevaba a mano
    /// (20000x12800 sobre un mapa de 20800x12800) y sus bichos no visitaban
    /// jamas la franja derecha.</summary>
    private double MapPointX() =>
        _rng.Next(Dials.MapMargin, (int)map.BoundsX - Dials.MapMargin);

    private double MapPointY() =>
        _rng.Next(Dials.MapMargin, (int)map.BoundsY - Dials.MapMargin);

    /// <summary>Un latido de IA: pensar (1/s) y, si toca, disparar.</summary>
    private void ThinkNpc(Entity npc)
    {
        if (!_npcAi.TryGetValue(npc.Id, out var ai)) return;
        var info = _npcInfo[npc.Id];

        RegenerateShield(npc);
        CheckFlee(npc, info, ai);

        if (_tick >= ai.NextThinkTick)
        {
            ai.NextThinkTick = _tick + ToTicks(Dials.AiThinkMs);
            switch (ai.State)
            {
                case NpcAiState.Searching: Search(npc, info, ai); break;
                case NpcAiState.Approaching: Approach(npc, info, ai); break;
                case NpcAiState.WaitingForPrey: WaitForMovement(npc, info, ai); break;
                case NpcAiState.Fleeing: Flee(ai); break;
            }
        }

        // dial `npc_combat_enabled`: apagado, los bichos siguen vagabundeando,
        // fichandote y persiguiendote — y el Vorax sigue huyendo malherido.
        // Lo unico que no ocurre es el daño.
        if (!npcCombatEnabled) return;
        if (!ai.Attacking || _tick < ai.NextShotTick) return;
        var prey = PreyOf(ai);
        if (prey is null) { ai.Forget(); return; }
        // fuera de alcance: espera
        if (Geometry.Distance(npc, prey.Entity) > Dials.NpcAttackRange) return;
        ai.NextShotTick = _tick + ToTicks(Dials.NpcAttackIntervalMs);
        DamagePlayer(npc, info, prey);
    }

    private void Search(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        var prey = NearestPlayer(npc, info.AggroRadius);
        if (prey is not null)
        {
            ai.TargetId = prey.Entity.Id;
            // los pasivos SIGUEN al jugador pero no abren fuego: solo devuelven
            // golpes (el ReceiveAttack del legado). El Ferox si es cazador.
            if (info.IsAggressive) ai.Attacking = true;
            ai.State = NpcAiState.Approaching;
            return;
        }
        // sin presa y quieto: a cruzar el mapa. Esto es lo que lo hace estar VIVO
        // en vez de girar sobre su propio eje.
        if (npc.Moving) return;
        npc.TargetX = MapPointX();
        npc.TargetY = MapPointY();
        ToThoseWhoSee(npc.Id, new EntityMoved(npc));
    }

    private void Approach(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        var prey = PreyOf(ai);
        if (LostPrey(npc, info, prey)) { ai.Forget(); return; }
        // un punto del circulo alrededor del jugador, no encima de el: asi los
        // bichos rodean en vez de amontonarse en el mismo pixel
        var (x, y) = Geometry.OnCircle(prey!.Entity.X, prey.Entity.Y,
            Dials.ApproachRadius, _rng.NextDouble() * Math.PI * 2, map);
        npc.TargetX = x;
        npc.TargetY = y;
        ToThoseWhoSee(npc.Id, new EntityMoved(npc));
        ai.State = NpcAiState.WaitingForPrey;
    }

    private void WaitForMovement(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        var prey = PreyOf(ai);
        if (LostPrey(npc, info, prey)) { ai.Forget(); return; }
        if (prey!.Entity.Moving) ai.State = NpcAiState.Approaching;
    }

    /// <summary>Se rinde si la presa ya no vale o se alejo demasiado.</summary>
    private static bool LostPrey(Entity npc, NpcSpawnInfo info, PlayerSlot? prey) =>
        prey is null
        || Geometry.Distance(npc, prey.Entity) > info.AggroRadius * Dials.DesaggroFactor;

    /// <summary>Los cobardes (`flee_hp_pct` &gt; 0) sueltan la presa y corren en cuanto
    /// el casco baja del umbral. El Vorax es el primero: te cuesta una fortuna
    /// bajarlo y, si te descuidas, se larga con el escudo regenerandose.</summary>
    private void CheckFlee(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        if (ai.State == NpcAiState.Fleeing) return;
        if (!Combat.ShouldFlee(npc, info.FleeHpPct)) return;

        var prey = PreyOf(ai);
        var (x, y) = Combat.FleeHeading(npc, prey?.Entity, map, _rng.NextDouble);
        npc.TargetX = x;
        npc.TargetY = y;
        ToThoseWhoSee(npc.Id, new EntityMoved(npc));

        ai.TargetId = 0;
        ai.Attacking = false;
        ai.State = NpcAiState.Fleeing;
        ai.FleeingUntilTick = _tick + ToTicks(Dials.FleeMs);
    }

    /// <summary>Mientras huye no piensa en nada mas. Cuando se le pasa el susto
    /// vuelve a buscar — con el escudo ya recompuesto si le dieron tregua.</summary>
    private void Flee(NpcAi ai)
    {
        if (_tick < ai.FleeingUntilTick) return;
        ai.Forget();
    }

    /// <summary>Escudo del NPC: 10% del maximo por segundo, tras 10 s sin recibir
    /// fuego (el CheckShieldPointsRepair del legado).</summary>
    private void RegenerateShield(Entity npc)
    {
        if (npc.Shield >= npc.MaxShield) return;
        if (_tick - npc.LastHitTick < ToTicks(Dials.NpcOutOfCombatMs)) return;
        if (_tick % ToTicks(Dials.NpcShieldRegenMs) != 0) return;
        Combat.RegenerateShield(npc);
    }

    /// <summary>La presa de un bicho, si todavia vale.
    ///
    /// La zona segura de la estacion protege a quien NO ha abierto fuego: ahi
    /// dentro nadie te ficha ni te dispara, por agresivo que sea. Pero si TU
    /// empezaste, te lo devuelve y te sigue hasta dentro — el DMZ es un refugio,
    /// no un parapeto desde el que disparar gratis.</summary>
    private PlayerSlot? PreyOf(NpcAi ai) =>
        ai.TargetId == 0
            ? null
            : _players.Values.FirstOrDefault(s => s.Entity.Id == ai.TargetId && !s.Dead
                && (ai.Provoked || !s.AtStation));

    /// <summary>El jugador vivo mas cercano dentro del radio. El legado recorria
    /// todos sin cortar y se quedaba con el ultimo; aqui gana el mas cercano.</summary>
    private PlayerSlot? NearestPlayer(Entity npc, uint radius)
    {
        PlayerSlot? best = null;
        var bestDist = double.MaxValue;
        foreach (var slot in _players.Values)
        {
            // la zona segura de la estacion es el DMZ del legado: ahi no se entra
            if (slot.Dead || slot.AtStation || slot.Disconnected) continue;
            var d = Geometry.Distance(npc, slot.Entity);
            if (d <= radius && d < bestDist) { bestDist = d; best = slot; }
        }
        return best;
    }

    private void DamagePlayer(Entity npc, NpcSpawnInfo info, PlayerSlot slot)
    {
        // el legado sorteaba ±10% sobre el daño base; se conserva
        var damage = Combat.WithVariance(info.Damage, _rng.Next);
        Combat.Absorb(slot.Entity, damage);

        ToThoseWhoSee(npc.Id, slot.Entity.Id, new AttackLanded(npc.Id, slot.Entity.Id, Weapon.Laser, damage,
            slot.Entity.Hp, slot.Entity.Shield, false, "ammo_cel_1", false));
        Send(slot, HeroStatsOf(slot));

        if (slot.Entity.Hp == 0) OnPlayerKilled(slot, npc);
    }
}
