// Acceso a datos del game server. Frontera de escritura (esquema-v1 §4):
// aqui solo se escribe game_session, player_ship_state y (en I5+) player_cargo_hold.
// Todo lo demas se LEE. Catalogos: solo lectura al arrancar.
using System.Security.Cryptography;
using Dapper;
using MySqlConnector;

namespace MexOrbit.GameServer.Data;

// Los tipos calcan el mapeo de MySqlConnector: INT UNSIGNED->uint,
// SMALLINT UNSIGNED->ushort, TINYINT UNSIGNED->byte, DECIMAL->decimal;
// los ids van con CAST AS SIGNED en el SQL para quedar como long.
public sealed record MapInfo(long Id, string Code, string DisplayName, uint BoundsX, uint BoundsY,
    uint StationX, uint StationY, uint SecureRange, string ZoneTier);

public sealed record MaterialBias(long ItemId, string LootId, decimal Weight);

public sealed record PortalInfo(long Id, uint X, uint Y, string TargetMapCode, bool IsWorking);

public sealed record RefineRecipe(long OutputItemId, string OutputLootId, uint OutputAmount,
    Dictionary<long, uint> Ingredients);

public sealed record NpcPrice(long ItemId, string LootId, decimal PriceCredits);

/// <summary>Resultado de descargar: lo que entro al almacen y lo que produjo el refinado.</summary>
public sealed record UnloadOutcome(Dictionary<long, uint> Stored, Dictionary<long, uint> Refined);

public sealed record NpcSpawnInfo(long CatalogId, string Code, string DisplayName, uint MaxHp, uint MaxShield,
    ushort Speed, uint Damage, bool IsAggressive, uint AggroRadius, uint RespawnSeconds, ushort Amount,
    uint RewardExperience, uint RewardHonor, uint RewardCredits, uint CargoDropMin, uint CargoDropMax);

public sealed record PlayerData(long AccountId, string PilotName, byte Faction, string ShipCode,
    uint BaseHp, ushort BaseSpeed, uint CargoCapacity, uint CurrentHp, uint CurrentShield,
    uint PosX, uint PosY, decimal Credits);

public sealed class Repo(string connectionString)
{
    private MySqlConnection Open() { var c = new MySqlConnection(connectionString); c.Open(); return c; }

    public MapInfo LoadStarterMap()
    {
        using var db = Open();
        return db.QuerySingle<MapInfo>(
            @"SELECT CAST(m.id AS SIGNED) AS Id, m.code, m.display_name AS DisplayName,
                     m.bounds_max_x AS BoundsX, m.bounds_max_y AS BoundsY,
                     s.pos_x AS StationX, s.pos_y AS StationY, s.secure_range AS SecureRange,
                     m.zone_tier AS ZoneTier
              FROM map m JOIN map_station s ON s.map_id = m.id
              WHERE m.is_starter = 1 LIMIT 1");
    }

    /// <summary>Portales visibles del mapa. Viajan completos en EnterMap: no entran
    /// por relevancia (spec del protocolo §relevancia por rango).</summary>
    public List<PortalInfo> LoadPortals(long mapId)
    {
        using var db = Open();
        return db.Query<PortalInfo>(
            @"SELECT CAST(p.id AS SIGNED) AS Id, p.pos_x AS X, p.pos_y AS Y,
                     d.code AS TargetMapCode, p.is_working AS IsWorking
              FROM map_portal p JOIN map d ON d.id = p.target_map_id
              WHERE p.map_id = @mapId AND p.is_visible = 1", new { mapId }).ToList();
    }

    public List<NpcSpawnInfo> LoadNpcSpawns(long mapId)
    {
        using var db = Open();
        return db.Query<NpcSpawnInfo>(
            @"SELECT CAST(n.id AS SIGNED) AS CatalogId, n.code, n.display_name AS DisplayName, n.max_hp AS MaxHp,
                     n.max_shield AS MaxShield, n.speed, n.damage,
                     n.is_aggressive AS IsAggressive, n.aggro_radius AS AggroRadius,
                     n.respawn_seconds AS RespawnSeconds, s.amount,
                     n.reward_experience AS RewardExperience, n.reward_honor AS RewardHonor,
                     n.reward_credits AS RewardCredits, n.cargo_drop_min AS CargoDropMin, n.cargo_drop_max AS CargoDropMax
              FROM map_npc_spawn s JOIN npc_catalog n ON n.id = s.npc_catalog_id
              WHERE s.map_id = @mapId", new { mapId }).ToList();
    }

    public PlayerData? LoadPlayer(long accountId)
    {
        using var db = Open();
        return db.QuerySingleOrDefault<PlayerData>(
            @"SELECT CAST(a.id AS SIGNED) AS AccountId, a.pilot_name AS PilotName, a.faction_id AS Faction,
                     c.code AS ShipCode, c.base_hp AS BaseHp, c.base_speed AS BaseSpeed,
                     c.cargo_capacity AS CargoCapacity,
                     st.current_hp AS CurrentHp, st.current_shield AS CurrentShield,
                     st.pos_x AS PosX, st.pos_y AS PosY,
                     CAST(COALESCE(rb.amount, 0) AS DECIMAL(20,6)) AS Credits
              FROM account a
              JOIN player_ship ps ON ps.account_id = a.id AND ps.is_active = 1
              JOIN ship_catalog c ON c.id = ps.ship_catalog_id
              JOIN player_ship_state st ON st.account_id = a.id
              LEFT JOIN player_resource_balance rb
                     ON rb.account_id = a.id
                    AND rb.server_item_id = (SELECT id FROM server_item WHERE item_key = 'credits')
              WHERE a.id = @accountId", new { accountId });
    }

    /// <summary>Cierra cualquier sesion viva de la cuenta y abre la nueva (sesion unica por diseño).</summary>
    public (long SessionId, string ReconnectToken) OpenSession(long accountId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        using var db = Open();
        db.Execute(
            @"UPDATE game_session SET closed_at = UTC_TIMESTAMP(), close_reason = 'REPLACED'
              WHERE account_id = @accountId AND closed_at IS NULL", new { accountId });
        var id = db.ExecuteScalar<long>(
            @"INSERT INTO game_session (account_id, reconnect_token_hash) VALUES (@accountId, @hash);
              SELECT LAST_INSERT_ID();", new { accountId, hash });
        return (id, token);
    }

    /// <summary>Busca la sesion viva dueña de un reconnect token (para el resume).</summary>
    public (long SessionId, long AccountId)? FindSessionByToken(string token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
        using var db = Open();
        return db.QuerySingleOrDefault<(long SessionId, long AccountId)?>(
            @"SELECT CAST(id AS SIGNED) AS SessionId, CAST(account_id AS SIGNED) AS AccountId
              FROM game_session
              WHERE reconnect_token_hash = @hash AND closed_at IS NULL
              LIMIT 1", new { hash });
    }

    public void CloseSession(long sessionId, string reason)
    {
        using var db = Open();
        db.Execute(
            @"UPDATE game_session SET closed_at = UTC_TIMESTAMP(), close_reason = @reason
              WHERE id = @sessionId AND closed_at IS NULL", new { sessionId, reason });
    }

    public void TouchSession(long sessionId)
    {
        using var db = Open();
        db.Execute("UPDATE game_session SET last_seen_at = UTC_TIMESTAMP() WHERE id = @sessionId", new { sessionId });
    }

    /// <summary>Sesgo de drops de la zona: la mezcla la fija la zona, el NPC la cantidad.</summary>
    public List<MaterialBias> LoadZoneBias(string zoneTier)
    {
        using var db = Open();
        return db.Query<MaterialBias>(
            @"SELECT CAST(b.server_item_id AS SIGNED) AS ItemId, i.loot_id AS LootId, b.weight
              FROM zone_drop_bias b JOIN server_item i ON i.id = b.server_item_id
              WHERE b.zone_tier = @zoneTier", new { zoneTier }).ToList();
    }

    /// <summary>Daño total de los láseres equipados (config 1) del jugador.</summary>
    public uint LoadLaserDamage(long accountId)
    {
        using var db = Open();
        return (uint)db.ExecuteScalar<decimal>(
            @"SELECT COALESCE(SUM(st.value), 0)
              FROM player_equipment_slot pes
              JOIN player_item pi ON pi.id = pes.player_item_id
              JOIN server_item_stat st ON st.server_item_id = pi.server_item_id
              JOIN server_item_stat_type t ON t.id = st.stat_type_id AND t.code = 'damage'
              WHERE pes.account_id = @accountId AND pes.ship_config = 1 AND pes.slot_kind = 'LASER'",
            new { accountId });
    }

    /// <summary>Capacidad de escudo: el base del casco mas lo que aporten los
    /// generadores equipados en la config activa (la Phoenix trae 0 de base: todo
    /// su escudo sale del NAN-1).</summary>
    public uint LoadShieldCapacity(long accountId)
    {
        using var db = Open();
        return (uint)db.ExecuteScalar<decimal>(
            @"SELECT (SELECT c.base_shield
                      FROM player_ship ps JOIN ship_catalog c ON c.id = ps.ship_catalog_id
                      WHERE ps.account_id = @accountId AND ps.is_active = 1)
                   + COALESCE((SELECT SUM(st.value)
                               FROM player_equipment_slot pes
                               JOIN player_item pi ON pi.id = pes.player_item_id
                               JOIN server_item_stat st ON st.server_item_id = pi.server_item_id
                               JOIN server_item_stat_type t ON t.id = st.stat_type_id AND t.code = 'shield'
                               WHERE pes.account_id = @accountId AND pes.ship_config = 1
                                 AND pes.slot_kind = 'GENERATOR'), 0)",
            new { accountId });
    }

    /// <summary>Bodega volante actual (item_id -> unidades).</summary>
    public Dictionary<long, uint> LoadCargo(long accountId)
    {
        using var db = Open();
        return db.Query<(long ItemId, uint Amount)>(
            @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
              FROM player_cargo_hold WHERE account_id = @accountId", new { accountId })
            .ToDictionary(r => r.ItemId, r => r.Amount);
    }

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

    /// <summary>La receta de refinado activa (30 Asterium + 20 Nebulium + 10 Coronium -> 1 Aurorium).</summary>
    public RefineRecipe? LoadRefineRecipe()
    {
        using var db = Open();
        var receta = db.QuerySingleOrDefault<(long Id, long OutputItemId, string OutputLootId, uint OutputAmount)?>(
            @"SELECT CAST(r.id AS SIGNED) AS Id, CAST(r.output_item_id AS SIGNED) AS OutputItemId,
                     i.loot_id AS OutputLootId, r.output_amount AS OutputAmount
              FROM refine_recipe r JOIN server_item i ON i.id = r.output_item_id
              WHERE r.is_active = 1 LIMIT 1");
        if (receta is null) return null;
        var ingredientes = db.Query<(long ItemId, uint Amount)>(
            @"SELECT CAST(server_item_id AS SIGNED) AS ItemId, amount
              FROM refine_recipe_ingredient WHERE recipe_id = @id", new { id = receta.Value.Id })
            .ToDictionary(r => r.ItemId, r => r.Amount);
        return new RefineRecipe(receta.Value.OutputItemId, receta.Value.OutputLootId,
            receta.Value.OutputAmount, ingredientes);
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

    public List<NpcPrice> LoadNpcPrices()
    {
        using var db = Open();
        return db.Query<NpcPrice>(
            @"SELECT CAST(p.server_item_id AS SIGNED) AS ItemId, i.loot_id AS LootId,
                     p.price_credits AS PriceCredits
              FROM npc_sell_price p JOIN server_item i ON i.id = p.server_item_id").ToList();
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

    /// <summary>Write-behind del estado en vivo: la UNICA escritura caliente (esquema-v1 §5).</summary>
    public void SaveShipState(long accountId, long mapId, uint x, uint y, uint hp, uint shield)
    {
        using var db = Open();
        db.Execute(
            @"UPDATE player_ship_state
              SET map_id = @mapId, pos_x = @x, pos_y = @y, current_hp = @hp, current_shield = @shield
              WHERE account_id = @accountId", new { accountId, mapId, x, y, hp, shield });
    }
}
