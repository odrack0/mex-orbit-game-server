// Todo lo que mueve material o credits.
//
// Dos reglas que no se negocian (esquema-v1 §4): las escrituras son SIEMPRE
// relativas —nunca "pon 500", siempre "suma 30"— y toda variacion deja su asiento
// en `economy_ledger`. Asi dos escritores concurrentes no se pisan y siempre se
// puede reconstruir como llego alguien a su saldo.
using Dapper;
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Infrastructure;

public sealed class EconomyRepository(string connectionString)
    : MySqlRepositorio(connectionString), IEconomyRepository
{
    /// <summary>Recolección: suma a la bodega volante + ledger, en una transacción.
    /// Escritor exclusivo: el game server (frontera del esquema §4).</summary>
    public void AddCargoPickup(long accountId, IEnumerable<(long ItemId, uint Amount)> items, long boxRef)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var (itemId, amount) in items)
        {
            db.Execute(
                @"INSERT INTO player_cargo_hold (account_id, server_item_id, amount)
                  VALUES (@accountId, @itemId, @amount)
                  ON DUPLICATE KEY UPDATE amount = amount + @amount",
                new { accountId, itemId, amount }, tx);
            db.Execute(
                @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason, ref_id)
                  VALUES (@accountId, @itemId, @amount, 'CARGO_PICKUP', @boxRef)",
                new { accountId, itemId, amount, boxRef }, tx);
        }
        tx.Commit();
    }

    /// <summary>Al morir: la bodega volante se vacia y queda asentada como
    /// CARGO_LOST, con la caja que la recibio como referencia. No se destruye
    /// nada — el material sigue en el mundo dentro de esa caja.</summary>
    public void ClearCargo(long accountId, long boxRef)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var filas = db.Query<(long ItemId, uint Amount)>(
            @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
              FROM player_cargo_hold WHERE account_id = @accountId", new { accountId }, tx).ToList();
        foreach (var (itemId, amount) in filas)
            db.Execute(
                @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason, ref_id)
                  VALUES (@accountId, @itemId, @delta, 'CARGO_LOST', @boxRef)",
                new { accountId, itemId, delta = -(long)amount, boxRef }, tx);
        db.Execute("DELETE FROM player_cargo_hold WHERE account_id = @accountId",
            new { accountId }, tx);
        tx.Commit();
    }

    /// <summary>Credits por matar NPC: escritura SIEMPRE relativa + ledger.</summary>
    public void AddCredits(long accountId, decimal delta, string reason, long? refId = null)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        db.Execute(
            @"INSERT INTO player_resource_balance (account_id, server_item_id, amount)
              SELECT @accountId, id, @delta FROM server_item WHERE item_key = 'credits'
              ON DUPLICATE KEY UPDATE amount = amount + @delta",
            new { accountId, delta }, tx);
        db.Execute(
            @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason, ref_id)
              SELECT @accountId, id, @delta, @reason, @refId FROM server_item WHERE item_key = 'credits'",
            new { accountId, delta, reason, refId }, tx);
        tx.Commit();
    }

    /// <summary>El almacen del jugador (loot_id -> cantidad), solo materiales.</summary>
    public List<(string LootId, decimal Amount)> LoadStorage(long accountId)
    {
        using var db = Open();
        return db.Query<(string LootId, decimal Amount)>(
            @"SELECT i.loot_id AS LootId, b.amount
              FROM player_resource_balance b
              JOIN server_item i ON i.id = b.server_item_id
              JOIN server_item_category c ON c.id = i.category_id
              WHERE b.account_id = @accountId AND c.code = 'material' AND b.amount > 0",
            new { accountId }).ToList();
    }

    /// <summary>Descarga en base: bodega -> almacen y refinado automatico, TODO en
    /// una transaccion. Escrituras siempre relativas (esquema-v1 §4).</summary>
    public UnloadOutcome UnloadAndRefine(long accountId, RefineRecipe? receta)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();

        var bodega = db.Query<(long ItemId, uint Amount)>(
            @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
              FROM player_cargo_hold WHERE account_id = @accountId AND amount > 0",
            new { accountId }, tx).ToDictionary(r => r.ItemId, r => r.Amount);
        if (bodega.Count == 0)
        {
            tx.Commit();
            return new UnloadOutcome(new(), new());
        }

        // 1) la bodega entra al almacen
        foreach (var (itemId, amount) in bodega)
        {
            db.Execute(
                @"INSERT INTO player_resource_balance (account_id, server_item_id, amount)
                  VALUES (@accountId, @itemId, @amount)
                  ON DUPLICATE KEY UPDATE amount = amount + @amount",
                new { accountId, itemId, amount }, tx);
            db.Execute(
                @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
                  VALUES (@accountId, @itemId, @amount, 'CARGO_UNLOAD')",
                new { accountId, itemId, amount }, tx);
        }
        db.Execute("DELETE FROM player_cargo_hold WHERE account_id = @accountId", new { accountId }, tx);

        // 2) refinado automatico y gratis: cuantos lotes completos alcanzan
        var refinado = new Dictionary<long, uint>();
        if (receta is not null && receta.Ingredients.Count > 0)
        {
            var saldos = db.Query<(long ItemId, decimal Amount)>(
                @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
                  FROM player_resource_balance WHERE account_id = @accountId",
                new { accountId }, tx).ToDictionary(r => r.ItemId, r => r.Amount);

            var lotes = uint.MaxValue;
            foreach (var (itemId, necesario) in receta.Ingredients)
            {
                var disponible = saldos.GetValueOrDefault(itemId, 0m);
                lotes = Math.Min(lotes, (uint)Math.Floor(disponible / necesario));
            }
            if (lotes is > 0 and < uint.MaxValue)
            {
                foreach (var (itemId, necesario) in receta.Ingredients)
                {
                    var consumo = necesario * lotes;
                    db.Execute(
                        @"UPDATE player_resource_balance SET amount = amount - @consumo
                          WHERE account_id = @accountId AND server_item_id = @itemId",
                        new { accountId, itemId, consumo }, tx);
                    db.Execute(
                        @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
                          VALUES (@accountId, @itemId, -@consumo, 'REFINE_IN')",
                        new { accountId, itemId, consumo }, tx);
                }
                var producido = receta.OutputAmount * lotes;
                db.Execute(
                    @"INSERT INTO player_resource_balance (account_id, server_item_id, amount)
                      VALUES (@accountId, @itemId, @producido)
                      ON DUPLICATE KEY UPDATE amount = amount + @producido",
                    new { accountId, itemId = receta.OutputItemId, producido }, tx);
                db.Execute(
                    @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
                      VALUES (@accountId, @itemId, @producido, 'REFINE_OUT')",
                    new { accountId, itemId = receta.OutputItemId, producido }, tx);
                refinado[receta.OutputItemId] = producido;
            }
        }

        tx.Commit();
        return new UnloadOutcome(bodega, refinado);
    }

    /// <summary>Venta al NPC: material del almacen -> credits, transaccional.
    /// `amount` 0 = todo. Devuelve (vendido, credits ganados, credits nuevos).</summary>
    public (uint Sold, decimal Gained, decimal NewCredits) SellToNpc(
        long accountId, long itemId, uint amount, decimal price)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var disponible = db.ExecuteScalar<decimal?>(
            @"SELECT amount FROM player_resource_balance
              WHERE account_id = @accountId AND server_item_id = @itemId FOR UPDATE",
            new { accountId, itemId }, tx) ?? 0m;
        var vender = amount == 0 ? (uint)Math.Floor(disponible) : Math.Min(amount, (uint)Math.Floor(disponible));
        if (vender == 0)
        {
            tx.Commit();
            return (0, 0m, 0m);
        }
        var ganado = price * vender;
        db.Execute(
            @"UPDATE player_resource_balance SET amount = amount - @vender
              WHERE account_id = @accountId AND server_item_id = @itemId",
            new { accountId, itemId, vender }, tx);
        db.Execute(
            @"INSERT INTO player_resource_balance (account_id, server_item_id, amount)
              SELECT @accountId, id, @ganado FROM server_item WHERE item_key = 'credits'
              ON DUPLICATE KEY UPDATE amount = amount + @ganado",
            new { accountId, ganado }, tx);
        db.Execute(
            @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
              VALUES (@accountId, @itemId, -@vender, 'NPC_SALE')",
            new { accountId, itemId, vender }, tx);
        db.Execute(
            @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
              SELECT @accountId, id, @ganado, 'NPC_SALE' FROM server_item WHERE item_key = 'credits'",
            new { accountId, ganado }, tx);
        var nuevos = db.ExecuteScalar<decimal>(
            @"SELECT amount FROM player_resource_balance
              WHERE account_id = @accountId
                AND server_item_id = (SELECT id FROM server_item WHERE item_key = 'credits')",
            new { accountId }, tx);
        tx.Commit();
        return (vender, ganado, nuevos);
    }
}
