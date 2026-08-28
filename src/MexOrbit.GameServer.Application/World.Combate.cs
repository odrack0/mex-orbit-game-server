// El laser del jugador, la caida del bicho, la muerte del piloto y su regreso.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    private void OnSelectTarget(SelectTargetCmd sel)
    {
        var slot = SlotDe(sel.Port);
        if (slot is null) return;
        if (sel.EntityId == 0 || !_npcs.TryGetValue(sel.EntityId, out var npc))
        {
            slot.TargetId = 0;
            slot.LaserOn = false;
            return;
        }
        slot.TargetId = sel.EntityId;
        Enviar(slot, new TargetAcquired(npc.Id, npc.Hp, npc.MaxHp, npc.Shield, npc.MaxShield));
    }

    private void OnLaserToggle(LaserToggleCmd laser)
    {
        var slot = SlotDe(laser.Port);
        if (slot is null) return;
        slot.LaserOn = !slot.Muerto && laser.Active && slot.TargetId != 0;
    }

    private void AplicarDanio(PlayerSlot slot, Entity npc)
    {
        // el escudo absorbe primero; los valores del evento son POST-daño, siempre
        var danio = slot.LaserDamage;
        Combate.Encajar(npc, danio);
        npc.LastHitTick = _tick;
        // ReceiveAttack del legado: quien le pega se vuelve su objetivo, sea el
        // bicho agresivo o no. Un pasivo no es un saco de boxeo: se defiende.
        if (_npcAi.TryGetValue(npc.Id, out var ai)) ai.Devolver(slot.Entity.Id);
        // el golpe lo frena en seco donde este (y avisa a todos)
        if (npc.Moving)
        {
            npc.Detener();
            Broadcast(new EntityMoved(npc));
        }
        Broadcast(new AttackLanded(slot.Entity.Id, npc.Id, Weapon.Laser, danio,
            npc.Hp, npc.Shield, false,
            // el aspecto del disparo: la municion equipada y si va potenciada.
            // En el slice hay una sola municion y el perfil de piloto llega en E4,
            // asi que van fijos; el contrato ya los transporta.
            slot.AmmoId, slot.Skilled));
        if (npc.Hp == 0) OnNpcMuerto(slot, npc);
    }

    private void OnNpcMuerto(PlayerSlot slot, Entity npc)
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
        Broadcast(new EntityDestroyed(npc.Id, slot.Entity.Id));
        _respawns.Add((_tick + info.RespawnSeconds * 1000 / (uint)tickMs, info, npc.Id));

        // recompensa: credits relativos + ledger (la api jamas toca esto en sesion)
        var credits = (decimal)info.RewardCredits;
        slot.Credits += credits;
        var accountId = slot.Data.AccountId;
        _ = Task.Run(() => Safe(() => economy.AddCredits(accountId, credits, "NPC_KILL", (long)npc.Id),
            "AddCredits"));
        Enviar(slot, HeroStatsDe(slot));

        // la caja: el NPC pone la cantidad, la ZONA pone la mezcla (§4 guidelines)
        var total = (uint)_rng.Next((int)info.CargoDropMin, (int)info.CargoDropMax + 1);
        var drops = Botin.Repartir(total, zoneBias);
        if (drops.Count == 0) return;
        SoltarCaja(npc.X, npc.Y, drops);
    }

    /// <summary>Deja una caja en el sitio y la anuncia. Las dos muertes —la del
    /// bicho y la del piloto— acaban aqui.</summary>
    private LootBox SoltarCaja(double x, double y, Dictionary<long, uint> drops)
    {
        var caja = new LootBox
        {
            Id = _nextBoxId++, X = x, Y = y, Drops = drops,
            ExpiraTick = _tick + EnTicks(Diales.BoxTtlMs),
        };
        _boxes[caja.Id] = caja;
        Broadcast(new BoxSpawned(caja.Id, "from_ship", caja.X, caja.Y));
        return caja;
    }

    /// <summary>Muerte del jugador. La bodega VOLANTE se queda en el sitio dentro
    /// de una caja: transferencia, no destruccion (guidelines §7). El almacen de
    /// la base no se toca — para eso esta separado del hold.</summary>
    private void OnJugadorMuerto(PlayerSlot slot, Entity asesino)
    {
        slot.Muerto = true;
        slot.LaserOn = false;
        slot.TargetId = 0;
        slot.Entity.Detener();
        foreach (var ai in _npcAi.Values.Where(a => a.TargetId == slot.Entity.Id)) ai.Olvidar();

        Broadcast(new EntityDestroyed(slot.Entity.Id, asesino.Id));

        if (slot.Cargo.Count > 0)
        {
            var caja = SoltarCaja(slot.Entity.X, slot.Entity.Y, new Dictionary<long, uint>(slot.Cargo));
            slot.Cargo.Clear();
            var id = slot.Data.AccountId;
            _ = Task.Run(() => Safe(() => economy.ClearCargo(id, (long)caja.Id), "ClearCargo"));
        }

        // en el slice hay una sola opcion; el contrato ya transporta coste y
        // disponibilidad para las demas
        Enviar(slot, new RespawnOffered(DeathCause.Npc, asesino.Name,
            [new RespawnChoice(1, "respawn.base", 0, true)]));
        log.LogInformation("cuenta {id} destruida por {npc}", slot.Data.AccountId, asesino.Name);
    }

    private void OnRespawnSelect(RespawnSelectCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null || !slot.Muerto) return;
        // en el slice hay una sola opcion: reaparecer en la base, entera y gratis
        slot.Muerto = false;
        slot.Entity.Hp = slot.Entity.MaxHp;
        slot.Entity.Shield = slot.Entity.MaxShield;
        slot.Entity.X = map.StationX;
        slot.Entity.Y = map.StationY;
        slot.Entity.Detener();
        Broadcast(new EntitySpawned(slot.Entity));
        Enviar(slot, HeroStatsDe(slot));
        ActualizarRangoBase(slot);
        GuardarNave(slot);
    }
}
