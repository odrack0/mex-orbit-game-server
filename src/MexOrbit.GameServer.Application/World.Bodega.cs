// La bodega volante, las cajas y la base: recoger, descargar y vender.
using MexOrbit.GameServer.Domain;
using Microsoft.Extensions.Logging;

namespace MexOrbit.GameServer.Application;

public sealed partial class World
{
    /// <summary>Entrar o salir del rango de la estacion abre/cierra su panel.</summary>
    private void ActualizarRangoBase(PlayerSlot slot)
    {
        var dist = Geometria.Distancia(map.StationX, map.StationY, slot.Entity.X, slot.Entity.Y);
        var dentro = dist <= map.SecureRange;
        if (dentro == slot.EnBase) return;
        slot.EnBase = dentro;
        Enviar(slot, new StationRangeChanged(dentro, (ulong)map.Id));
    }

    // ─── recoleccion ────────────────────────────────────────────────────────

    private void OnCollectBox(CollectBoxCmd collect)
    {
        var slot = SlotDe(collect.Port);
        if (slot is null) return;
        if (!_boxes.TryGetValue(collect.BoxId, out var caja))
        {
            Enviar(slot, new Failed(collect.RequestId, ErrorCode.Gone));
            return;
        }
        // la validacion que el legado dejaba al cliente, donde debe estar: aqui
        var dist = Geometria.Distancia(caja.X, caja.Y, slot.Entity.X, slot.Entity.Y);
        if (dist > Diales.CollectRange)
        {
            Enviar(slot, new Failed(collect.RequestId, ErrorCode.TooFar));
            return;
        }
        var espacio = slot.Data.CargoCapacity - slot.CargoUsed;
        if (espacio == 0)
        {
            Enviar(slot, new Failed(collect.RequestId, ErrorCode.Insufficient, "bodega llena"));
            return;
        }

        // toma lo que quepa; el resto queda en la caja hasta su expiracion
        var tomados = Botin.Tomar(caja, espacio);

        try
        {
            // sincrono a proposito: el resultado solo sale si la BD ya lo tiene
            economy.AddCargoPickup(slot.Data.AccountId, tomados, (long)caja.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo AddCargoPickup cuenta {id}", slot.Data.AccountId);
            Enviar(slot, new Failed(collect.RequestId, ErrorCode.Generic));
            return;
        }
        foreach (var (itemId, amount) in tomados)
            slot.Cargo[itemId] = slot.Cargo.GetValueOrDefault(itemId) + amount;

        Enviar(slot, new Collected(collect.RequestId, [.. tomados.Select(EnMaterial)]));
        Enviar(slot, HeroStatsDe(slot));

        if (caja.Drops.Count == 0)
        {
            _boxes.Remove(caja.Id);
            AQuienesVenCaja(caja.Id, new BoxDespawned(caja.Id, BoxDespawnReason.Collected));
            OlvidarCaja(caja.Id);
        }
    }

    // ─── la base ────────────────────────────────────────────────────────────

    private void OnUnloadCargo(UnloadCargoCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null) return;
        if (!EstaEnBase(slot, cmd.RequestId)) return;

        UnloadOutcome resultado;
        try
        {
            // sincrono: la respuesta solo sale si la BD ya lo tiene
            resultado = economy.UnloadAndRefine(slot.Data.AccountId, refineRecipe);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo UnloadAndRefine cuenta {id}", slot.Data.AccountId);
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.Generic));
            return;
        }
        slot.Cargo.Clear();
        Enviar(slot, new Unloaded(cmd.RequestId,
            [.. resultado.Stored.Select(EnMaterial)],
            [.. resultado.Refined.Select(EnMaterial)]));
        Enviar(slot, HeroStatsDe(slot));
        EnviarAlmacen(slot);
    }

    private void OnSellToNpc(SellToNpcCmd cmd)
    {
        var slot = SlotDe(cmd.Port);
        if (slot is null) return;
        if (!EstaEnBase(slot, cmd.RequestId)) return;

        if (!_preciosPorLoot.TryGetValue(cmd.MaterialId, out var precio))
        {
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.Invalid, "el NPC no compra eso"));
            return;
        }
        (uint Sold, decimal Gained, decimal NewCredits) venta;
        try
        {
            venta = economy.SellToNpc(slot.Data.AccountId, precio.ItemId, (uint)cmd.Amount,
                precio.PriceCredits);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "fallo SellToNpc cuenta {id}", slot.Data.AccountId);
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.Generic));
            return;
        }
        if (venta.Sold == 0)
        {
            Enviar(slot, new Failed(cmd.RequestId, ErrorCode.Insufficient, "sin existencias"));
            return;
        }
        slot.Credits = venta.NewCredits;
        Enviar(slot, new Sold(cmd.RequestId, (ulong)venta.Gained, (ulong)venta.NewCredits));
        Enviar(slot, HeroStatsDe(slot));
        EnviarAlmacen(slot);
    }

    private bool EstaEnBase(PlayerSlot slot, ulong requestId)
    {
        if (slot.EnBase) return true;
        Enviar(slot, new Failed(requestId, ErrorCode.TooFar, "fuera de la base"));
        return false;
    }

    private void EnviarAlmacen(PlayerSlot slot)
    {
        var accountId = slot.Data.AccountId;
        var port = slot.Port;
        // lectura fuera del hilo del tick: el estado ya se persistio
        _ = Task.Run(() => Safe(() =>
        {
            var saldos = economy.LoadStorage(accountId);
            Enviar(port, new StorageSynced(
                [.. saldos.Select(s => new MaterialAmount(s.LootId, (uint)s.Amount))]));
        }, "EnviarAlmacen"));
    }

    /// <summary>El id interno (`server_item_id`) jamas sale del server: fuera
    /// viaja el `loot_id` publico del catalogo.</summary>
    private MaterialAmount EnMaterial(KeyValuePair<long, uint> par) =>
        new(_lootIds[par.Key], par.Value);

    private MaterialAmount EnMaterial((long ItemId, uint Amount) par) =>
        new(_lootIds[par.ItemId], par.Amount);
}
