// Los catalogos de JUEGO: que bichos pueblan un mapa, que suelta la zona, que
// compra el NPC y que se refina. Solo lectura, y solo al levantar el mapa.
using Dapper;
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Infrastructure;

public sealed class GameCatalog(string connectionString)
    : MySqlRepositorio(connectionString), IGameCatalog
{
    public List<NpcSpawnInfo> LoadNpcSpawns(long mapId)
    {
        using var db = Open();
        return db.Query<NpcSpawnInfo>(
            @"SELECT CAST(n.id AS SIGNED) AS CatalogId, n.code, n.display_name AS DisplayName, n.max_hp AS MaxHp,
                     n.max_shield AS MaxShield, n.speed, n.damage,
                     n.is_aggressive AS IsAggressive, n.flee_hp_pct AS FleeHpPct,
                     n.aggro_radius AS AggroRadius,
                     n.respawn_seconds AS RespawnSeconds, s.amount,
                     n.reward_experience AS RewardExperience, n.reward_honor AS RewardHonor,
                     n.reward_credits AS RewardCredits, n.cargo_drop_min AS CargoDropMin, n.cargo_drop_max AS CargoDropMax
              FROM map_npc_spawn s JOIN npc_catalog n ON n.id = s.npc_catalog_id
              WHERE s.map_id = @mapId", new { mapId }).ToList();
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

    public List<NpcPrice> LoadNpcPrices()
    {
        using var db = Open();
        return db.Query<NpcPrice>(
            @"SELECT CAST(p.server_item_id AS SIGNED) AS ItemId, i.loot_id AS LootId,
                     p.price_credits AS PriceCredits
              FROM npc_sell_price p JOIN server_item i ON i.id = p.server_item_id").ToList();
    }
}

/// <summary>Los diales de JUEGO que viven en BD con su auditoria, no en
/// appsettings: mover uno queda asentado en `server_setting_audit`.</summary>
public sealed class ServerSettings(string connectionString)
    : MySqlRepositorio(connectionString), IServerSettings
{
    /// <summary>Escalar booleano de `server_setting`. Si la fila no existe se usa
    /// el valor por defecto: un dial ausente jamas debe tumbar el arranque.</summary>
    public bool LoadBoolSetting(string key, bool porDefecto)
    {
        using var db = Open();
        var v = db.ExecuteScalar<string?>(
            "SELECT value FROM server_setting WHERE setting_key = @key", new { key });
        if (v is null) return porDefecto;
        return v is "1" or "true" or "TRUE" or "True";
    }
}
