// Verificacion del game ticket (JWT Ed25519 emitido por la api).
// El game server solo tiene la clave PUBLICA: no puede emitir, solo validar.
// jti de un solo uso: un ticket repetido se rechaza (diseno auth-v1).
//
// Devuelve un `ErrorCode` del dominio y no una cadena. Antes devolvia el codigo
// en SCREAMING_SNAKE y quien llamaba hacia un `Enum.Parse` sobre una conversion a
// PascalCase hecha a mano: un error tipografico aqui salia como excepcion alla.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MexOrbit.GameServer.Application;
using MexOrbit.GameServer.Domain;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MexOrbit.GameServer.Infrastructure;

public sealed class Ed25519TicketVerifier : ITicketVerifier
{
    private readonly Ed25519PublicKeyParameters _pub;
    private readonly ConcurrentDictionary<string, long> _jtisVistos = new();

    public Ed25519TicketVerifier(string publicKeyPath)
    {
        _pub = new Ed25519PublicKeyParameters(File.ReadAllBytes(publicKeyPath), 0);
    }

    /// <summary>Valida firma, expiracion, audiencia, version y unicidad del jti.</summary>
    public (long AccountId, ErrorCode? Error) Verify(string jwt, int expectedProtocolVersion)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return (0, ErrorCode.BadTicket);

        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signer = new Ed25519Signer();
        signer.Init(false, _pub);
        signer.BlockUpdate(signingInput, 0, signingInput.Length);
        if (!signer.VerifySignature(FromB64Url(parts[2]))) return (0, ErrorCode.BadTicket);

        JsonElement payload;
        try { payload = JsonDocument.Parse(FromB64Url(parts[1])).RootElement; }
        catch { return (0, ErrorCode.BadTicket); }

        var ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload.GetProperty("aud").GetString() != "game") return (0, ErrorCode.BadTicket);
        if (payload.GetProperty("exp").GetInt64() < ahora) return (0, ErrorCode.BadTicket);
        if (payload.GetProperty("pv").GetInt32() != expectedProtocolVersion)
            return (0, ErrorCode.VersionUnsupported);

        var jti = payload.GetProperty("jti").GetString() ?? "";
        // un solo uso: si ya lo vimos (y no ha expirado de la memoria), se rechaza
        LimpiarJtis(ahora);
        if (!_jtisVistos.TryAdd(jti, payload.GetProperty("exp").GetInt64()))
            return (0, ErrorCode.BadTicket);

        return (long.Parse(payload.GetProperty("sub").GetString()!), null);
    }

    private void LimpiarJtis(long ahora)
    {
        foreach (var (jti, exp) in _jtisVistos)
            if (exp < ahora)
                _jtisVistos.TryRemove(jti, out _);
    }

    private static byte[] FromB64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

/// <summary>El reloj de pared. Uno solo, inyectado, para que no vuelva a haber
/// `DateTime.Now` disperso por la simulacion.</summary>
public sealed class SystemClock : IClock
{
    public long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
