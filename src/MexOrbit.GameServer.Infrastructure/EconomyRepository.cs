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
    : MySqlRepository(connectionString), IEconomyRepository
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
        var rows = db.Query<(long ItemId, uint Amount)>(
            @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
              FROM player_cargo_hold WHERE account_id = @accountId", new { accountId }, tx).ToList();
        foreach (var (itemId, amount) in rows)
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
    public UnloadOutcome UnloadAndRefine(long accountId, RefineRecipe? recipe)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();

        var hold = db.Query<(long ItemId, uint Amount)>(
            @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
              FROM player_cargo_hold WHERE account_id = @accountId AND amount > 0",
            new { accountId }, tx).ToDictionary(r => r.ItemId, r => r.Amount);
        if (hold.Count == 0)
        {
            tx.Commit();
            return new UnloadOutcome(new(), new());
        }

        // 1) la bodega entra al almacen
        foreach (var (itemId, amount) in hold)
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
        var refined = new Dictionary<long, uint>();
        if (recipe is not null && recipe.Ingredients.Count > 0)
        {
            var balances = db.Query<(long ItemId, decimal Amount)>(
                @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
                  FROM player_resource_balance WHERE account_id = @accountId",
                new { accountId }, tx).ToDictionary(r => r.ItemId, r => r.Amount);

            var batches = uint.MaxValue;
            foreach (var (itemId, needed) in recipe.Ingredients)
            {
                var available = balances.GetValueOrDefault(itemId, 0m);
                batches = Math.Min(batches, (uint)Math.Floor(available / needed));
            }
            if (batches is > 0 and < uint.MaxValue)
            {
                foreach (var (itemId, needed) in recipe.Ingredients)
                {
                    var consumed = needed * batches;
                    db.Execute(
                        @"UPDATE player_resource_balance SET amount = amount - @consumed
                          WHERE account_id = @accountId AND server_item_id = @itemId",
                        new { accountId, itemId, consumed }, tx);
                    db.Execute(
                        @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
                          VALUES (@accountId, @itemId, -@consumed, 'REFINE_IN')",
                        new { accountId, itemId, consumed }, tx);
                }
                var produced = recipe.OutputAmount * batches;
                db.Execute(
                    @"INSERT INTO player_resource_balance (account_id, server_item_id, amount)
                      VALUES (@accountId, @itemId, @produced)
                      ON DUPLICATE KEY UPDATE amount = amount + @produced",
                    new { accountId, itemId = recipe.OutputItemId, produced }, tx);
                db.Execute(
                    @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
                      VALUES (@accountId, @itemId, @produced, 'REFINE_OUT')",
                    new { accountId, itemId = recipe.OutputItemId, produced }, tx);
                refined[recipe.OutputItemId] = produced;
            }
        }

        tx.Commit();
        return new UnloadOutcome(hold, refined);
    }

    /// <summary>Venta al NPC: material del almacen -> credits, transaccional.
    /// `amount` 0 = todo. Devuelve (vendido, credits ganados, credits nuevos).</summary>
    public (uint Sold, decimal Gained, decimal NewCredits) SellToNpc(
        long accountId, long itemId, uint amount, decimal price)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var available = db.ExecuteScalar<decimal?>(
            @"SELECT amount FROM player_resource_balance
              WHERE account_id = @accountId AND server_item_id = @itemId FOR UPDATE",
            new { accountId, itemId }, tx) ?? 0m;
        var toSell = amount == 0 ? (uint)Math.Floor(available) : Math.Min(amount, (uint)Math.Floor(available));
        if (toSell == 0)
        {
            tx.Commit();
            return (0, 0m, 0m);
        }
        var earned = price * toSell;
        db.Execute(
            @"UPDATE player_resource_balance SET amount = amount - @toSell
              WHERE account_id = @accountId AND server_item_id = @itemId",
            new { accountId, itemId, toSell }, tx);
        db.Execute(
            @"INSERT INTO player_resource_balance (account_id, server_item_id, amount)
              SELECT @accountId, id, @earned FROM server_item WHERE item_key = 'credits'
              ON DUPLICATE KEY UPDATE amount = amount + @earned",
            new { accountId, earned }, tx);
        db.Execute(
            @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
              VALUES (@accountId, @itemId, -@toSell, 'NPC_SALE')",
            new { accountId, itemId, toSell }, tx);
        db.Execute(
            @"INSERT INTO economy_ledger (account_id, server_item_id, delta, reason)
              SELECT @accountId, id, @earned, 'NPC_SALE' FROM server_item WHERE item_key = 'credits'",
            new { accountId, earned }, tx);
        var newBalance = db.ExecuteScalar<decimal>(
            @"SELECT amount FROM player_resource_balance
              WHERE account_id = @accountId
                AND server_item_id = (SELECT id FROM server_item WHERE item_key = 'credits')",
            new { accountId }, tx);
        tx.Commit();
        return (toSell, earned, newBalance);
    }
}
