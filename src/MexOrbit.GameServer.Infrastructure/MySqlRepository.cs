// La base comun de los adaptadores de MySQL.
//
// Frontera de escritura (esquema-v1 §4): este server solo escribe game_session,
// player_ship_state, player_cargo_hold, player_resource_balance y economy_ledger.
// Todo lo demas se LEE. Catalogos: solo lectura al arrancar.
//
// Los tipos calcan el mapeo de MySqlConnector: INT UNSIGNED->uint,
// SMALLINT UNSIGNED->ushort, TINYINT UNSIGNED->byte, DECIMAL->decimal;
// los ids van con CAST AS SIGNED en el SQL para quedar como long.
using MySqlConnector;

namespace MexOrbit.GameServer.Infrastructure;

public abstract class MySqlRepository(string connectionString)
{
    protected MySqlConnection Open()
    {
        var c = new MySqlConnection(connectionString);
        c.Open();
        return c;
    }
}
