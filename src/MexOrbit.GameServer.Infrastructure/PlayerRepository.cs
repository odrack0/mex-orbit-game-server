// El jugador, su nave y sus stats derivadas del equipo.
using Dapper;
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Infrastructure;

public sealed class PlayerRepository(string connectionString)
    : MySqlRepository(connectionString), IPlayerRepository
{
    public PlayerData? LoadPlayer(long accountId)
    {
        using var db = Open();
        return db.QuerySingleOrDefault<PlayerData>(
            @"SELECT CAST(a.id AS SIGNED) AS AccountId, a.pilot_name AS PilotName, a.faction_id AS Faction,
                     c.code AS ShipCode, c.base_hp AS BaseHp, c.base_speed AS BaseSpeed,
                     c.cargo_capacity AS CargoCapacity,
                     st.current_hp AS CurrentHp, st.current_shield AS CurrentShield,
                     st.pos_x AS PosX, st.pos_y AS PosY,
                     CAST(COALESCE(rb.amount, 0) AS DECIMAL(20,6)) AS Credits,
                     CAST(st.map_id AS SIGNED) AS MapId
              FROM account a
              JOIN player_ship ps ON ps.account_id = a.id AND ps.is_active = 1
              JOIN ship_catalog c ON c.id = ps.ship_catalog_id
              JOIN player_ship_state st ON st.account_id = a.id
              LEFT JOIN player_resource_balance rb
                     ON rb.account_id = a.id
                    AND rb.server_item_id = (SELECT id FROM server_item WHERE item_key = 'credits')
              WHERE a.id = @accountId", new { accountId });
        // OJO: Dapper casa los records posicionales por ORDEN DE COLUMNA, no por
        // nombre. `MapId` va al final del SELECT porque va al final del record;
        // ponerlo en medio compila igual y revienta en tiempo de ejecucion.
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
