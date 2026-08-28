// La bodega volante, las cajas y la base: recoger, descargar y vender.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    /// <summary>Entrar o salir del rango de la estacion abre/cierra su panel.</summary>
    private void UpdateStationRange(PlayerSlot slot)
    {
        var dist = Geometry.Distance(map.StationX, map.StationY, slot.Entity.X, slot.Entity.Y);
        var inside = dist <= map.SecureRange;
        if (inside == slot.AtStation) return;
        slot.AtStation = inside;
        Send(slot, new StationRangeChanged(inside, (ulong)map.Id));
    }

    // ─── recoleccion ────────────────────────────────────────────────────────

    private void OnCollectBox(CollectBoxCmd collect)
    {
        var slot = SlotOf(collect.Port);
        if (slot is null) return;
        if (!_boxes.TryGetValue(collect.BoxId, out var box))
        {
            Send(slot, new Failed(collect.RequestId, ErrorCode.Gone));
            return;
        }
        // la validacion que el legado dejaba al cliente, donde debe estar: aqui
        var dist = Geometry.Distance(box.X, box.Y, slot.Entity.X, slot.Entity.Y);
        if (dist > Dials.CollectRange)
        {
            Send(slot, new Failed(collect.RequestId, ErrorCode.TooFar));
            return;
        }
        var space = slot.Data.CargoCapacity - slot.CargoUsed;
        if (space == 0)
        {
            Send(slot, new Failed(collect.RequestId, ErrorCode.Insufficient, "bodega llena"));
            return;
        }

        // toma lo que quepa; el resto queda en la caja hasta su expiracion
        var taken = Loot.Take(box, space);

        try
        {
            // sincrono a proposito: el resultado solo sale si la BD ya lo tiene
            economy.AddCargoPickup(slot.Data.AccountId, taken, (long)box.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo AddCargoPickup cuenta {id}", slot.Data.AccountId);
            Send(slot, new Failed(collect.RequestId, ErrorCode.Generic));
            return;
        }
        foreach (var (itemId, amount) in taken)
            slot.Cargo[itemId] = slot.Cargo.GetValueOrDefault(itemId) + amount;

        Send(slot, new Collected(collect.RequestId, [.. taken.Select(ToMaterial)]));
        Send(slot, HeroStatsOf(slot));

        if (box.Drops.Count == 0)
        {
            _boxes.Remove(box.Id);
            ToThoseWhoSeeBox(box.Id, new BoxDespawned(box.Id, BoxDespawnReason.Collected));
            ForgetBox(box.Id);
        }
    }

    // ─── la base ────────────────────────────────────────────────────────────

    private void OnUnloadCargo(UnloadCargoCmd cmd)
    {
        var slot = SlotOf(cmd.Port);
        if (slot is null) return;
        if (!EnsureAtStation(slot, cmd.RequestId)) return;

        UnloadOutcome outcome;
        try
        {
            // sincrono: la respuesta solo sale si la BD ya lo tiene
            outcome = economy.UnloadAndRefine(slot.Data.AccountId, refineRecipe);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo UnloadAndRefine cuenta {id}", slot.Data.AccountId);
            Send(slot, new Failed(cmd.RequestId, ErrorCode.Generic));
            return;
        }
        slot.Cargo.Clear();
        Send(slot, new Unloaded(cmd.RequestId,
            [.. outcome.Stored.Select(ToMaterial)],
            [.. outcome.Refined.Select(ToMaterial)]));
        Send(slot, HeroStatsOf(slot));
        SendStorage(slot);
    }

    private void OnSellToNpc(SellToNpcCmd cmd)
    {
        var slot = SlotOf(cmd.Port);
        if (slot is null) return;
        if (!EnsureAtStation(slot, cmd.RequestId)) return;

        if (!_preciosPorLoot.TryGetValue(cmd.MaterialId, out var price))
        {
            Send(slot, new Failed(cmd.RequestId, ErrorCode.Invalid, "el NPC no compra eso"));
            return;
        }
        (uint Sold, decimal Gained, decimal NewCredits) sale;
        try
        {
            sale = economy.SellToNpc(slot.Data.AccountId, price.ItemId, (uint)cmd.Amount,
                price.PriceCredits);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo SellToNpc cuenta {id}", slot.Data.AccountId);
            Send(slot, new Failed(cmd.RequestId, ErrorCode.Generic));
            return;
        }
        if (sale.Sold == 0)
        {
            Send(slot, new Failed(cmd.RequestId, ErrorCode.Insufficient, "sin existencias"));
            return;
        }
        slot.Credits = sale.NewCredits;
        Send(slot, new Sold(cmd.RequestId, (ulong)sale.Gained, (ulong)sale.NewCredits));
        Send(slot, HeroStatsOf(slot));
        SendStorage(slot);
    }

    private bool EnsureAtStation(PlayerSlot slot, ulong requestId)
    {
        if (slot.AtStation) return true;
        Send(slot, new Failed(requestId, ErrorCode.TooFar, "fuera de la base"));
        return false;
    }

    private void SendStorage(PlayerSlot slot)
    {
        var accountId = slot.Data.AccountId;
        var port = slot.Port;
        // lectura fuera del hilo del tick: el estado ya se persistio
        _ = Task.Run(() => Safe(() =>
        {
            var balances = economy.LoadStorage(accountId);
            Send(port, new StorageSynced(
                [.. balances.Select(s => new MaterialAmount(s.LootId, (uint)s.Amount))]));
        }, "EnviarAlmacen"));
    }

    /// <summary>El id interno (`server_item_id`) jamas sale del server: fuera
    /// viaja el `loot_id` publico del catalogo.</summary>
    private MaterialAmount ToMaterial(KeyValuePair<long, uint> par) =>
        new(_lootIds[par.Key], par.Value);

    private MaterialAmount ToMaterial((long ItemId, uint Amount) par) =>
        new(_lootIds[par.ItemId], par.Amount);
}
