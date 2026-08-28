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
        var (x, y) = PuntoDeAparicion();
        e.X = x;
        e.Y = y;
        e.Detener();
        _npcs[e.Id] = e;
        _npcInfo[e.Id] = spawn;
        _npcAi[e.Id] = new NpcAi
        {
            ProximoPensamientoTick = _tick + _rng.Next(0, EnTicks(Diales.AiThinkMs)),
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
    private (double X, double Y) PuntoDeAparicion()
    {
        (double X, double Y) punto = (0, 0);
        for (var intento = 0; intento < 12; intento++)
        {
            punto = (PuntoDelMapaX(), PuntoDelMapaY());
            if (_players.Values.All(s =>
                    Geometria.Distancia(punto.X, punto.Y, s.Entity.X, s.Entity.Y) > rangos.Entidades))
                return punto;
        }
        return punto;
    }

    /// <summary>Los limites salen del MAPA. El legado los llevaba a mano
    /// (20000x12800 sobre un mapa de 20800x12800) y sus bichos no visitaban
    /// jamas la franja derecha.</summary>
    private double PuntoDelMapaX() =>
        _rng.Next(Diales.MargenDelMapa, (int)map.BoundsX - Diales.MargenDelMapa);

    private double PuntoDelMapaY() =>
        _rng.Next(Diales.MargenDelMapa, (int)map.BoundsY - Diales.MargenDelMapa);

    /// <summary>Un latido de IA: pensar (1/s) y, si toca, disparar.</summary>
    private void PensarNpc(Entity npc)
    {
        if (!_npcAi.TryGetValue(npc.Id, out var ai)) return;
        var info = _npcInfo[npc.Id];

        RegenerarEscudo(npc);
        ComprobarHuida(npc, info, ai);

        if (_tick >= ai.ProximoPensamientoTick)
        {
            ai.ProximoPensamientoTick = _tick + EnTicks(Diales.AiThinkMs);
            switch (ai.Estado)
            {
                case NpcAiState.Buscando: Buscar(npc, info, ai); break;
                case NpcAiState.VolandoAlEnemigo: Aproximarse(npc, info, ai); break;
                case NpcAiState.EsperandoQueSeMueva: EsperarMovimiento(npc, info, ai); break;
                case NpcAiState.Huyendo: Huir(ai); break;
            }
        }

        // dial `npc_combat_enabled`: apagado, los bichos siguen vagabundeando,
        // fichandote y persiguiendote — y el Vorax sigue huyendo malherido.
        // Lo unico que no ocurre es el daño.
        if (!npcCombatEnabled) return;
        if (!ai.Atacando || _tick < ai.ProximoDisparoTick) return;
        var presa = PresaDe(ai);
        if (presa is null) { ai.Olvidar(); return; }
        // fuera de alcance: espera
        if (Geometria.Distancia(npc, presa.Entity) > Diales.NpcAttackRange) return;
        ai.ProximoDisparoTick = _tick + EnTicks(Diales.NpcAttackIntervalMs);
        AplicarDanioAJugador(npc, info, presa);
    }

    private void Buscar(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        var presa = JugadorMasCercano(npc, info.AggroRadius);
        if (presa is not null)
        {
            ai.TargetId = presa.Entity.Id;
            // los pasivos SIGUEN al jugador pero no abren fuego: solo devuelven
            // golpes (el ReceiveAttack del legado). El Ferox si es cazador.
            if (info.IsAggressive) ai.Atacando = true;
            ai.Estado = NpcAiState.VolandoAlEnemigo;
            return;
        }
        // sin presa y quieto: a cruzar el mapa. Esto es lo que lo hace estar VIVO
        // en vez de girar sobre su propio eje.
        if (npc.Moving) return;
        npc.TargetX = PuntoDelMapaX();
        npc.TargetY = PuntoDelMapaY();
        AQuienesVen(npc.Id, new EntityMoved(npc));
    }

    private void Aproximarse(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        var presa = PresaDe(ai);
        if (SePerdio(npc, info, presa)) { ai.Olvidar(); return; }
        // un punto del circulo alrededor del jugador, no encima de el: asi los
        // bichos rodean en vez de amontonarse en el mismo pixel
        var (x, y) = Geometria.EnElCirculo(presa!.Entity.X, presa.Entity.Y,
            Diales.AproximacionRadio, _rng.NextDouble() * Math.PI * 2, map);
        npc.TargetX = x;
        npc.TargetY = y;
        AQuienesVen(npc.Id, new EntityMoved(npc));
        ai.Estado = NpcAiState.EsperandoQueSeMueva;
    }

    private void EsperarMovimiento(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        var presa = PresaDe(ai);
        if (SePerdio(npc, info, presa)) { ai.Olvidar(); return; }
        if (presa!.Entity.Moving) ai.Estado = NpcAiState.VolandoAlEnemigo;
    }

    /// <summary>Se rinde si la presa ya no vale o se alejo demasiado.</summary>
    private static bool SePerdio(Entity npc, NpcSpawnInfo info, PlayerSlot? presa) =>
        presa is null
        || Geometria.Distancia(npc, presa.Entity) > info.AggroRadius * Diales.DesaggroFactor;

    /// <summary>Los cobardes (`flee_hp_pct` &gt; 0) sueltan la presa y corren en cuanto
    /// el casco baja del umbral. El Vorax es el primero: te cuesta una fortuna
    /// bajarlo y, si te descuidas, se larga con el escudo regenerandose.</summary>
    private void ComprobarHuida(Entity npc, NpcSpawnInfo info, NpcAi ai)
    {
        if (ai.Estado == NpcAiState.Huyendo) return;
        if (!Combate.DebeHuir(npc, info.FleeHpPct)) return;

        var presa = PresaDe(ai);
        var (x, y) = Combate.RumboDeHuida(npc, presa?.Entity, map, _rng.NextDouble);
        npc.TargetX = x;
        npc.TargetY = y;
        AQuienesVen(npc.Id, new EntityMoved(npc));

        ai.TargetId = 0;
        ai.Atacando = false;
        ai.Estado = NpcAiState.Huyendo;
        ai.HuyendoHastaTick = _tick + EnTicks(Diales.HuidaMs);
    }

    /// <summary>Mientras huye no piensa en nada mas. Cuando se le pasa el susto
    /// vuelve a buscar — con el escudo ya recompuesto si le dieron tregua.</summary>
    private void Huir(NpcAi ai)
    {
        if (_tick < ai.HuyendoHastaTick) return;
        ai.Olvidar();
    }

    /// <summary>Escudo del NPC: 10% del maximo por segundo, tras 10 s sin recibir
    /// fuego (el CheckShieldPointsRepair del legado).</summary>
    private void RegenerarEscudo(Entity npc)
    {
        if (npc.Shield >= npc.MaxShield) return;
        if (_tick - npc.LastHitTick < EnTicks(Diales.NpcOutOfCombatMs)) return;
        if (_tick % EnTicks(Diales.NpcShieldRegenMs) != 0) return;
        Combate.RegenerarEscudo(npc);
    }

    private PlayerSlot? PresaDe(NpcAi ai) =>
        ai.TargetId == 0
            ? null
            : _players.Values.FirstOrDefault(s => s.Entity.Id == ai.TargetId && !s.Muerto && !s.EnBase);

    /// <summary>El jugador vivo mas cercano dentro del radio. El legado recorria
    /// todos sin cortar y se quedaba con el ultimo; aqui gana el mas cercano.</summary>
    private PlayerSlot? JugadorMasCercano(Entity npc, uint radio)
    {
        PlayerSlot? mejor = null;
        var mejorDist = double.MaxValue;
        foreach (var slot in _players.Values)
        {
            // la zona segura de la estacion es el DMZ del legado: ahi no se entra
            if (slot.Muerto || slot.EnBase || slot.Desconectado) continue;
            var d = Geometria.Distancia(npc, slot.Entity);
            if (d <= radio && d < mejorDist) { mejorDist = d; mejor = slot; }
        }
        return mejor;
    }

    private void AplicarDanioAJugador(Entity npc, NpcSpawnInfo info, PlayerSlot slot)
    {
        // el legado sorteaba ±10% sobre el daño base; se conserva
        var danio = Combate.ConVariacion(info.Damage, _rng.Next);
        Combate.Encajar(slot.Entity, danio);

        AQuienesVen(npc.Id, slot.Entity.Id, new AttackLanded(npc.Id, slot.Entity.Id, Weapon.Laser, danio,
            slot.Entity.Hp, slot.Entity.Shield, false, "ammo_cel_1", false));
        Enviar(slot, HeroStatsDe(slot));

        if (slot.Entity.Hp == 0) OnJugadorMuerto(slot, npc);
    }
}
