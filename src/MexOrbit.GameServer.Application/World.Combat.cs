// El laser del jugador, la caida del bicho, la muerte del piloto y su regreso.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    private void OnSelectTarget(SelectTargetCmd sel)
    {
        var slot = SlotOf(sel.Port);
        if (slot is null) return;
        // No se puede fichar lo que no se ve. El cliente solo puede pinchar sobre
        // lo que recibio, asi que en el juego limpio esto no cambia nada; lo que
        // cierra es el atajo de mandar un id cualquiera para que el server te
        // mantenga en relevancia —y te informe de— un bicho al otro lado del mapa.
        if (sel.EntityId == 0
            || !_npcs.TryGetValue(sel.EntityId, out var npc)
            || !slot.SeenEntities.Contains(sel.EntityId))
        {
            slot.TargetId = 0;
            slot.LaserOn = false;
            return;
        }
        slot.TargetId = sel.EntityId;
        slot.WarnedOutOfRange = false;   // objetivo nuevo, aviso nuevo
        Send(slot, new TargetAcquired(npc.Id, npc.Hp, npc.MaxHp, npc.Shield, npc.MaxShield));
    }

    private void OnLaserToggle(LaserToggleCmd laser)
    {
        var slot = SlotOf(laser.Port);
        if (slot is null) return;
        slot.LaserOn = !slot.Dead && laser.Active && slot.TargetId != 0;
        if (!slot.LaserOn) slot.WarnedOutOfRange = false;
    }

    private void ApplyDamage(PlayerSlot slot, Entity npc)
    {
        // el escudo absorbe primero; los valores del evento son POST-daño, siempre
        var damage = slot.LaserDamage;
        Combat.Absorb(npc, damage);
        npc.LastHitTick = _tick;
        // ReceiveAttack del legado: quien le pega se vuelve su objetivo, sea el
        // bicho agresivo o no. Un pasivo no es un saco de boxeo: se defiende.
        if (_npcAi.TryGetValue(npc.Id, out var ai)) ai.FightBack(slot.Entity.Id);
        // el golpe lo frena en seco donde este (y avisa a todos)
        if (npc.Moving)
        {
            npc.Stop();
            ToThoseWhoSee(npc.Id, new EntityMoved(npc));
        }
        ToThoseWhoSee(slot.Entity.Id, npc.Id, new AttackLanded(slot.Entity.Id, npc.Id, Weapon.Laser, damage,
            npc.Hp, npc.Shield, false,
            // el aspecto del disparo: la municion equipada y si va potenciada.
            // En el slice hay una sola municion y el perfil de piloto llega en E4,
            // asi que van fijos; el contrato ya los transporta.
            slot.AmmoId, slot.Skilled));
        if (npc.Hp == 0) OnNpcKilled(slot, npc);
    }

    private void OnNpcKilled(PlayerSlot slot, Entity npc)
    {
        var info = _npcInfo[npc.Id];
        _npcs.Remove(npc.Id);
        _npcInfo.Remove(npc.Id);
        _npcAi.Remove(npc.Id);
        foreach (var s in _players.Values.Where(s => s.TargetId == npc.Id))
        {
            s.TargetId = 0;
            s.LaserOn = false;
        }
        ToThoseWhoSee(npc.Id, new EntityDestroyed(npc.Id, slot.Entity.Id));
        // el cliente ya borro el nodo: hay que olvidarlo o su reaparicion —con el
        // MISMO id— no le llegaria nunca
        ForgetEntity(npc.Id);
        _respawns.Add((_tick + info.RespawnSeconds * 1000 / (uint)tickMs, info, npc.Id));

        // recompensa: credits relativos + ledger (la api jamas toca esto en sesion)
        var credits = (decimal)info.RewardCredits;
        slot.Credits += credits;
        var accountId = slot.Data.AccountId;
        _ = Task.Run(() => Safe(() => economy.AddCredits(accountId, credits, "NPC_KILL", (long)npc.Id),
            "AddCredits"));
        Send(slot, HeroStatsOf(slot));

        // la caja: el NPC pone la cantidad, la ZONA pone la mezcla (§4 guidelines)
        var total = (uint)_rng.Next((int)info.CargoDropMin, (int)info.CargoDropMax + 1);
        var drops = Loot.Distribute(total, zoneBias);
        if (drops.Count == 0) return;
        DropBox(npc.X, npc.Y, drops);
    }

    /// <summary>Deja una caja en el sitio y la anuncia. Las dos muertes —la del
    /// bicho y la del piloto— acaban aqui.</summary>
    private LootBox DropBox(double x, double y, Dictionary<long, uint> drops)
    {
        var box = new LootBox
        {
            Id = _nextBoxId++, X = x, Y = y, Drops = drops,
            ExpiraTick = _tick + ToTicks(Dials.BoxTtlMs),
        };
        _boxes[box.Id] = box;
        // no se anuncia aqui: quien la tenga cerca la recibe en el paso de relevancia
        return box;
    }

    /// <summary>Muerte del jugador. La bodega VOLANTE se queda en el sitio dentro
    /// de una caja: transferencia, no destruccion (guidelines §7). El almacen de
    /// la base no se toca — para eso esta separado del hold.</summary>
    private void OnPlayerKilled(PlayerSlot slot, Entity killer)
    {
        slot.Dead = true;
        slot.LaserOn = false;
        slot.TargetId = 0;
        slot.Entity.Stop();
        foreach (var ai in _npcAi.Values.Where(a => a.TargetId == slot.Entity.Id)) ai.Forget();

        ToThoseWhoSee(slot.Entity.Id, new EntityDestroyed(slot.Entity.Id, killer.Id));
        ForgetEntity(slot.Entity.Id);

        if (slot.Cargo.Count > 0)
        {
            var box = DropBox(slot.Entity.X, slot.Entity.Y, new Dictionary<long, uint>(slot.Cargo));
            slot.Cargo.Clear();
            var id = slot.Data.AccountId;
            _ = Task.Run(() => Safe(() => economy.ClearCargo(id, (long)box.Id), "ClearCargo"));
        }

        // en el slice hay una sola opcion; el contrato ya transporta coste y
        // disponibilidad para las demas
        Send(slot, new RespawnOffered(DeathCause.Npc, killer.Name,
            [new RespawnChoice(1, "respawn.base", 0, true)]));
        log.LogInformation("cuenta {id} destruida por {npc}", slot.Data.AccountId, killer.Name);
    }

    private void OnRespawnSelect(RespawnSelectCmd cmd)
    {
        var slot = SlotOf(cmd.Port);
        if (slot is null || !slot.Dead) return;
        // en el slice hay una sola opcion: reaparecer en la base, entera y gratis
        slot.Dead = false;
        slot.Entity.Hp = slot.Entity.MaxHp;
        slot.Entity.Shield = slot.Entity.MaxShield;
        slot.Entity.X = map.StationX;
        slot.Entity.Y = map.StationY;
        slot.Entity.Stop();
        // solo a el: su cliente borro la nave al recibir EntityDestroyed. Los demas
        // la vuelven a recibir por relevancia, ya en la base
        Send(slot, new EntitySpawned(slot.Entity));
        Send(slot, HeroStatsOf(slot));
        UpdateStationRange(slot);
        SaveShip(slot);
    }
}
