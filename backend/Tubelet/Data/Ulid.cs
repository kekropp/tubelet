using System.Security.Cryptography;

namespace Tubelet.Data;

/// <summary>
/// Minimal ULID for custom playlist ids ("TL-&lt;26 chars&gt;"): 48-bit millisecond timestamp +
/// 80 bits of randomness, Crockford base32. Lexicographically sortable and collision-safe enough
/// for hand-created playlists. The "TL-" prefix distinguishes custom playlists from YouTube "PL…" ids.
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford (no I L O U)

    public static string NewPlaylistId() => "TL-" + New();

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--) { bytes[i] = (byte)(ms & 0xFF); ms >>= 8; }
        RandomNumberGenerator.Fill(bytes[6..]);
        return Encode(bytes);
    }

    // 128 bits → 26 base32 chars. 26*5 = 130, so the value is left-padded with 2 zero bits.
    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        Span<char> outp = stackalloc char[26];
        for (var c = 0; c < 26; c++)
        {
            var acc = 0;
            for (var k = 0; k < 5; k++)
            {
                var globalBit = c * 5 + k - 2; // first 2 bits are padding zeros
                acc <<= 1;
                if (globalBit >= 0 && globalBit < 128)
                    acc |= (bytes[globalBit / 8] >> (7 - globalBit % 8)) & 1;
            }
            outp[c] = Alphabet[acc];
        }
        return new string(outp);
    }
}
