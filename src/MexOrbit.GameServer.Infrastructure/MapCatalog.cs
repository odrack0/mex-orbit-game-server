// Los mapas y su topologia.
using Dapper;
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;

namespace MexOrbit.GameServer.Infrastructure;

public sealed class MapCatalog(string connectionString)
    : MySqlRepository(connectionString), IMapCatalog
{
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

    /// <summary>Un mapa por su id. Lo usa la reconexion para saber a que mundo
    /// devolver al jugador.</summary>
    public MapInfo? LoadMapById(long mapId)
    {
        using var db = Open();
        var code = db.ExecuteScalar<string?>("SELECT code FROM map WHERE id = @mapId", new { mapId });
        return code == null ? null : LoadMap(code);
    }

    /// <summary>Donde reconectar para un mapa. Sin fila, el mapa no se sirve.</summary>
    public MapServer? LoadMapServer(long mapId)
    {
        using var db = Open();
        var f = db.QuerySingleOrDefault(
            @"SELECT host AS Host, port AS Port, is_tls AS IsTls
              FROM map_server WHERE map_id = @mapId LIMIT 1", new { mapId });
        return f == null ? null : new MapServer((string)f.Host, (ushort)f.Port, (bool)f.IsTls);
    }

    /// <summary>Un mapa por su codigo. El LEFT JOIN a la estacion es deliberado:
    /// solo el 1-1 tiene base, y un mapa sin ella no puede dejar de cargarse por
    /// eso. Sin estacion, `SecureRange` sale 0 y el cliente no dibuja ninguna.</summary>
    public MapInfo? LoadMap(string code)
    {
        using var db = Open();
        // Se lee a fila suelta y se arma a mano en vez de dejarselo a Dapper: un
        // COALESCE en MySQL promueve la columna a BIGINT, y Dapper exige que el
        // constructor del record case EXACTO —no convierte Int64 a UInt32— asi que
        // el server ni arrancaba. Armarlo aqui cuesta cuatro lineas y no depende
        // de como MySQL decida tipar una expresion.
        var f = db.QuerySingleOrDefault(
            @"SELECT m.id AS Id, m.code AS Code, m.display_name AS DisplayName,
                     m.bounds_max_x AS BoundsX, m.bounds_max_y AS BoundsY,
                     s.pos_x AS StationX, s.pos_y AS StationY, s.secure_range AS SecureRange,
                     m.zone_tier AS ZoneTier
              FROM map m LEFT JOIN map_station s ON s.map_id = m.id
              WHERE m.code = @code LIMIT 1", new { code });
        if (f == null) return null;
        // Sin estacion, rango 0: el cliente entiende que ese mapa no tiene base.
        return new MapInfo((long)f.Id, (string)f.Code, (string)f.DisplayName,
            (uint)f.BoundsX, (uint)f.BoundsY,
            f.StationX == null ? 0u : (uint)f.StationX,
            f.StationY == null ? 0u : (uint)f.StationY,
            f.SecureRange == null ? 0u : (uint)f.SecureRange,
            (string)f.ZoneTier);
    }

    /// <summary>Portales visibles del mapa. Viajan completos en EnterMap: no entran
    /// por relevancia (spec del protocolo §relevancia por rango).</summary>
    public List<PortalInfo> LoadPortals(long mapId)
    {
        using var db = Open();
        return db.Query<PortalInfo>(
            @"SELECT CAST(p.id AS SIGNED) AS Id, p.pos_x AS X, p.pos_y AS Y,
                     d.code AS TargetMapCode, p.is_working AS IsWorking,
                     p.target_pos_x AS TargetX, p.target_pos_y AS TargetY
              FROM map_portal p JOIN map d ON d.id = p.target_map_id
              WHERE p.map_id = @mapId AND p.is_visible = 1", new { mapId }).ToList();
    }
}
