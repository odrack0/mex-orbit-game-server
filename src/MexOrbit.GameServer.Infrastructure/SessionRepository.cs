// Sesion unica por cuenta. El reconnect token nunca se guarda en claro: solo su
// hash, igual que una contraseña.
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MexOrbit.GameServer.Application;

namespace MexOrbit.GameServer.Infrastructure;

public sealed class SessionRepository(string connectionString)
    : MySqlRepositorio(connectionString), ISessionRepository
{
    /// <summary>Cierra cualquier sesion viva de la cuenta y abre la nueva (sesion unica por diseño).</summary>
    public (long SessionId, string ReconnectToken) OpenSession(long accountId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Hash(token);
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
        var hash = Hash(token);
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
        db.Execute("UPDATE game_session SET last_seen_at = UTC_TIMESTAMP() WHERE id = @sessionId",
            new { sessionId });
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
