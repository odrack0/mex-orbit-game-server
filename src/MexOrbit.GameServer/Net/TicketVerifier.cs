// Verificacion del game ticket (JWT Ed25519 emitido por la api).
// El game server solo tiene la clave PUBLICA: no puede emitir, solo validar.
// jti de un solo uso: un ticket repetido se rechaza (diseno auth-v1).
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MexOrbit.GameServer.Net;

public sealed class TicketVerifier
{
    private readonly Ed25519PublicKeyParameters _pub;
    private readonly ConcurrentDictionary<string, long> _jtisVistos = new();

    public TicketVerifier(string publicKeyPath)
    {
        _pub = new Ed25519PublicKeyParameters(File.ReadAllBytes(publicKeyPath), 0);
    }

    /// <summary>Valida firma, expiracion, audiencia, version y unicidad del jti.</summary>
    public (long AccountId, string? Error) Verify(string jwt, int expectedProtocolVersion)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return (0, "BAD_TICKET");

        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signer = new Ed25519Signer();
        signer.Init(false, _pub);
        signer.BlockUpdate(signingInput, 0, signingInput.Length);
        if (!signer.VerifySignature(FromB64Url(parts[2]))) return (0, "BAD_TICKET");

        JsonElement payload;
        try { payload = JsonDocument.Parse(FromB64Url(parts[1])).RootElement; }
        catch { return (0, "BAD_TICKET"); }

        var ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload.GetProperty("aud").GetString() != "game") return (0, "BAD_TICKET");
        if (payload.GetProperty("exp").GetInt64() < ahora) return (0, "BAD_TICKET");
        if (payload.GetProperty("pv").GetInt32() != expectedProtocolVersion) return (0, "VERSION_UNSUPPORTED");

        var jti = payload.GetProperty("jti").GetString() ?? "";
        // un solo uso: si ya lo vimos (y no ha expirado de la memoria), se rechaza
        LimpiarJtis(ahora);
        if (!_jtisVistos.TryAdd(jti, payload.GetProperty("exp").GetInt64())) return (0, "BAD_TICKET");

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
